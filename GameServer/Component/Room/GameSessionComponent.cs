using Common.Logging;
using GameServer.Component.Player;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Room;

/// <summary>
/// 게임 진행 중 상태를 관리하는 컴포넌트. RoomComponent와 1:1로 생성된다.
/// 몬스터 AI 틱은 내부 PeriodicTimer(100ms)로 구동된다.
/// </summary>
public class GameSessionComponent
{
    private static long _monsterIdSeq;
    private static ulong NextMonsterId() => (ulong)Interlocked.Increment(ref _monsterIdSeq);

    private readonly RoomComponent  _room;
    private readonly Dictionary<ulong, MonsterComponent> _monsters = new();
    private readonly WaveSpawner    _waveSpawner  = new();
    private readonly GemManager     _gemManager   = new();
    private readonly WeaponSystem   _weaponSystem = new();
    private readonly object _stateLock = new();
    private int   _endedFlag;
    private int   _cleanupCounter;
    private float _survivalElapsed;       // 총 생존 시간(초)
    private float _survivalBroadcastAcc;  // 10초 브로드캐스트 누적
    private const float ClearTimeSec = 1800f; // 30분 생존 시 클리어

    public ulong RoomId => _room.RoomId;

    private CancellationTokenSource _cts = new();

    public GameSessionComponent(RoomComponent room)
    {
        _room = room;
    }

    // 플레이어 스폰 위치 — 맵 중앙 기준 (3200x2400)
    private static readonly (float X, float Y)[] SpawnPoints =
    [
        (1500f, 1200f),
        (1700f, 1200f),
        (1600f, 1100f),
        (1600f, 1300f),
    ];

    public void Start(IReadOnlyList<PlayerComponent> players)
    {
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            (float X, float Y) spawn = i < SpawnPoints.Length ? SpawnPoints[i] : (400f, 300f);
            // 이전 게임 세션 잔류 패킷 폐기 — HP 복원 전에 실행하여 이전 ReqMove가 새 세션에서 처리되지 않도록 방지
            p.Session.ClearPacketQueue();
            p.Character.RestoreFullHp();
            p.World.SetPosition(spawn.X, spawn.Y);
        }

        foreach (var p in players)
            _weaponSystem.Register(p);

        SpawnMonsters();
        SendInitialState(players);
        _ = RunTickAsync(_cts.Token);
        GameLogger.Info($"GameSession:{RoomId}", $"게임 세션 시작 (플레이어 {players.Count}명, 몬스터 {_monsters.Count}개)");
    }

    private void SpawnMonsters()
    {
        // 초기 웨이브 — 맵 중앙(1600, 1200) 주변 스폰
        void Add(MonsterComponent m) => _monsters[m.MonsterId] = m;
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Bat,    1200, 900));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Bat,    2000, 900));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Zombie,  900, 1500));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Zombie, 2300, 1500));
    }

    /// <summary>
    /// WaveSpawner가 반환한 스폰 목록을 _monsters에 추가하고 pending에 적재한다.
    /// _stateLock 하에서 호출된다.
    /// </summary>
    private void DoWaveSpawn(List<(MonsterType Type, float X, float Y)> spawns, List<GamePacket> pending)
    {
        foreach (var (type, x, y) in spawns)
        {
            var m = new MonsterComponent(NextMonsterId(), type, x, y);
            _monsters[m.MonsterId] = m;
            pending.Add(new GamePacket
            {
                NotiSpawnMonster = new NotiSpawnMonster
                {
                    MonsterId = m.MonsterId, MonsterType = (int)m.Type,
                    X = m.X, Y = m.Y, Hp = m.Hp, MaxHp = m.MaxHp
                }
            });
        }

        pending.Add(new GamePacket
        {
            NotiWaveStart = new NotiWaveStart
            {
                WaveNumber = _waveSpawner.WaveNumber, MonsterCount = spawns.Count
            }
        });

        GameLogger.Info($"GameSession:{RoomId}",
            $"웨이브 {_waveSpawner.WaveNumber} 시작 — {spawns.Count}마리 스폰 (총 {_monsters.Count}마리)");
    }

    private void SendInitialState(IReadOnlyList<PlayerComponent> players)
    {
        var res = new ResEnterGame { ErrorCode = ErrorCode.Success };

        foreach (var p in players)
            res.Players.Add(BuildPlayerInfo(p));

        foreach (var m in _monsters.Values)
            res.Monsters.Add(BuildMonsterInfo(m));

        _room.BroadcastPacket(new GamePacket { ResEnterGame = res });
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
                Tick(0.1f);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GameLogger.Error($"GameSession:{RoomId}", "틱 루프 예외", ex);
        }
    }

    private void Tick(float dt)
    {
        if (_endedFlag == 1) return;

        var pending = new List<GamePacket>();

        lock (_stateLock)
        {
            // 생존 타이머
            _survivalElapsed      += dt;
            _survivalBroadcastAcc += dt;
            if (_survivalBroadcastAcc >= 10f)
            {
                _survivalBroadcastAcc = 0f;
                pending.Add(new GamePacket
                {
                    NotiSurvivalTime = new NotiSurvivalTime { ElapsedSeconds = (int)_survivalElapsed }
                });
            }

            if (_survivalElapsed >= ClearTimeSec)
            {
                EndGame(true, pending);
            }
            else
            {
                var alivePlayers = _room.GetPlayers().Where(p => p.Character.IsAlive).ToList();
                List<MonsterMoveInfo>? movedList = null;

                foreach (var monster in _monsters.Values)
                {
                    // 가장 가까운 살아있는 플레이어 탐색 — wrap-aware 최단 경로
                    float targetX        = monster.X;
                    float targetY        = monster.Y;
                    float nearestDistSq  = float.MaxValue;
                    PlayerComponent? nearestPlayer = null;

                    foreach (var p in alivePlayers)
                    {
                        // 맵 순환: 직선 거리 vs 감싸는 거리 중 짧은 쪽 선택
                        float dx = p.World.X - monster.X;
                        float dy = p.World.Y - monster.Y;
                        if (dx >  1600f) dx -= 3200f; else if (dx < -1600f) dx += 3200f;
                        if (dy >  1200f) dy -= 2400f; else if (dy < -1200f) dy += 2400f;
                        float distSq = dx * dx + dy * dy;
                        if (distSq < nearestDistSq)
                        {
                            nearestDistSq = distSq;
                            nearestPlayer = p;
                            // wrap-aware 오프셋을 몬스터 기준 절대 좌표로 변환
                            targetX = monster.X + dx;
                            targetY = monster.Y + dy;
                        }
                    }

                    var (respawned, moved) = monster.Tick(dt, targetX, targetY);

                    if (respawned)
                    {
                        pending.Add(new GamePacket
                        {
                            NotiRespawn = new NotiRespawn { MonsterId = monster.MonsterId, X = monster.X, Y = monster.Y, Hp = monster.Hp }
                        });
                        continue;
                    }

                    if (moved)
                    {
                        movedList ??= [];
                        movedList.Add(new MonsterMoveInfo { MonsterId = monster.MonsterId, X = monster.X, Y = monster.Y });
                    }

                    // 공격 — 살아있고, 쿨다운 해제됐고, AttackRange 이내일 때만
                    if (!monster.IsAlive || nearestPlayer == null || !monster.ShouldAttack()) continue;
                    float attackRangeSq = monster.AttackRange * monster.AttackRange;
                    if (nearestDistSq > attackRangeSq) continue;

                    int damage = CalcDamage(monster.Atk, nearestPlayer.Character.Defense);
                    bool died  = nearestPlayer.Character.TakeDamage(damage);

                    pending.Add(new GamePacket
                    {
                        NotiMonsterAttack = new NotiMonsterAttack
                        {
                            MonsterId = monster.MonsterId, TargetPlayerId = nearestPlayer.AccountId, Damage = damage
                        }
                    });
                    pending.Add(new GamePacket
                    {
                        NotiHpChange = new NotiHpChange
                        {
                            EntityId = nearestPlayer.AccountId, Hp = nearestPlayer.Character.Hp,
                            MaxHp = nearestPlayer.Character.MaxHp, IsMonster = false
                        }
                    });

                    if (died)
                    {
                        pending.Add(new GamePacket
                        {
                            NotiDeath = new NotiDeath { EntityId = nearestPlayer.AccountId, IsMonster = false }
                        });
                        CheckAllPlayersDead(pending);
                    }
                }

                // 이동한 몬스터들을 단일 패킷으로 배치
                if (movedList is { Count: > 0 })
                {
                    var movePacket = new NotiMonsterMove();
                    movePacket.Moves.AddRange(movedList);
                    pending.Add(new GamePacket { NotiMonsterMove = movePacket });
                }

                // 리스폰 없는 죽은 몬스터 정리 (보스 계열) — 매 틱 대신 10틱(1초)마다
                _cleanupCounter++;
                if (_cleanupCounter >= 10)
                {
                    _cleanupCounter = 0;
                    foreach (var id in _monsters.Values
                        .Where(m => !m.IsAlive && !m.CanRespawn)
                        .Select(m => m.MonsterId)
                        .ToList())
                    {
                        _monsters.Remove(id);
                    }
                }

                // 서버사이드 자동 무기 틱
                var weaponHits = _weaponSystem.Tick(dt, alivePlayers, _monsters.Values);
                foreach (var (attackerId, monsterId, dmg) in weaponHits)
                    ApplyWeaponHit(attackerId, monsterId, dmg, pending);

                // 웨이브 스포너 틱
                var waveSpawns = _waveSpawner.Tick(dt, _monsters.Count);
                if (waveSpawns != null) DoWaveSpawn(waveSpawns, pending);
            }
        }

        foreach (var pkt in pending)
            _room.BroadcastPacket(pkt);
    }

    // PlayerRpgController → PlayerComponent 워커 스레드에서 호출 (_stateLock으로 Tick과 직렬화)
    public void ProcessAttack(PlayerComponent player, ulong monsterId)
    {
        if (_endedFlag == 1) return;

        var pending = new List<GamePacket>();

        lock (_stateLock)
        {
            if (!player.Character.IsAlive) return;
            if (!player.World.CanAttack()) return;

            if (!_monsters.TryGetValue(monsterId, out var monster) || !monster.IsAlive) return;

            int damage = CalcDamage(player.Character.Attack, monster.Def);
            player.World.ResetAttackCooldown();
            bool died = monster.TakeDamage(damage);

            pending.Add(new GamePacket
            {
                NotiCombat = new NotiCombat
                {
                    AttackerPlayerId = player.AccountId, TargetMonsterId = monsterId, Damage = damage
                }
            });
            pending.Add(new GamePacket
            {
                NotiHpChange = new NotiHpChange
                {
                    EntityId = monsterId, Hp = monster.Hp, MaxHp = monster.MaxHp, IsMonster = true
                }
            });

            if (died)
            {
                pending.Add(new GamePacket
                {
                    NotiDeath = new NotiDeath { EntityId = monsterId, IsMonster = true }
                });
                SpawnGem(monster.X, monster.Y, monster.ExpReward, pending);
                if (monster.IsBoss) EndGame(true, pending);
            }
        }

        foreach (var pkt in pending)
            _room.BroadcastPacket(pkt);
    }

    public void ProcessMove(PlayerComponent player, float x, float y)
    {
        if (_endedFlag == 1 || !player.Character.IsAlive) return;

        var pending = new List<GamePacket>();

        lock (_stateLock)
        {
            player.World.Move(x, y);
            pending.Add(new GamePacket
            {
                NotiMove = new NotiMove { PlayerId = player.AccountId, X = player.World.X, Y = player.World.Y }
            });
            // 이동 후 주변 젬 자동 수집
            CollectGems(player, pending);
        }

        foreach (var pkt in pending)
            _room.BroadcastPacket(pkt);
    }

    public void ProcessChooseWeapon(PlayerComponent player, int weaponId)
    {
        if (_endedFlag == 1) return;
        lock (_stateLock)
        {
            _weaponSystem.ApplyChoice(player, weaponId);
        }
    }

    public void ProcessChat(PlayerComponent player, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500) return;
        _room.BroadcastPacket(new GamePacket
        {
            NotiGameChat = new NotiGameChat { PlayerId = player.AccountId, PlayerName = player.Name, Message = message }
        });
    }

    /// <summary>젬 스폰 — 몬스터 사망 시 호출. _stateLock 하에서 실행된다.</summary>
    private void SpawnGem(float x, float y, int expValue, List<GamePacket> pending)
    {
        var gem = _gemManager.Spawn(x, y, expValue);
        pending.Add(new GamePacket
        {
            NotiGemSpawn = new NotiGemSpawn { GemId = gem.Id, X = gem.X, Y = gem.Y, ExpValue = gem.ExpValue }
        });
    }

    /// <summary>젬 수집 후 EXP 지급. _stateLock 하에서 실행된다.</summary>
    private void CollectGems(PlayerComponent player, List<GamePacket> pending)
    {
        var collected = _gemManager.CollectNearby(player.World.X, player.World.Y);
        foreach (var gem in collected)
        {
            bool leveled = player.Character.GainExp(gem.ExpValue);
            pending.Add(new GamePacket
            {
                NotiGemCollect = new NotiGemCollect
                {
                    GemId = gem.Id, PlayerId = player.AccountId, ExpGained = gem.ExpValue
                }
            });
            _ = player.Session.SendAsync(new GamePacket
            {
                NotiExpGain = new NotiExpGain
                {
                    PlayerId = player.AccountId, ExpGained = gem.ExpValue,
                    TotalExp = player.Character.Exp, NextLevelExp = player.Character.NextLevelExp
                }
            });
            if (leveled)
            {
                pending.Add(new GamePacket
                {
                    NotiLevelUp = new NotiLevelUp
                    {
                        PlayerId   = player.AccountId, NewLevel   = player.Character.Level,
                        NewMaxHp   = player.Character.MaxHp,  NewAttack  = player.Character.Attack,
                        NewDefense = player.Character.Defense
                    }
                });

                // 레벨업 → 무기 선택지 발송 (해당 플레이어에게만)
                SendWeaponChoices(player);
            }
        }
    }

    /// <summary>자동 무기 히트 처리. _stateLock 하에서 실행된다.</summary>
    private void ApplyWeaponHit(ulong attackerAccountId, ulong monsterId, int damage, List<GamePacket> pending)
    {
        if (!_monsters.TryGetValue(monsterId, out var monster) || !monster.IsAlive) return;

        bool died = monster.TakeDamage(damage);

        pending.Add(new GamePacket
        {
            NotiCombat = new NotiCombat
            {
                AttackerPlayerId = attackerAccountId, TargetMonsterId = monsterId, Damage = damage
            }
        });
        pending.Add(new GamePacket
        {
            NotiHpChange = new NotiHpChange
            {
                EntityId = monsterId, Hp = monster.Hp, MaxHp = monster.MaxHp, IsMonster = true
            }
        });

        if (!died) return;

        pending.Add(new GamePacket
        {
            NotiDeath = new NotiDeath { EntityId = monsterId, IsMonster = true }
        });

        SpawnGem(monster.X, monster.Y, monster.ExpReward, pending);

        if (monster.IsBoss) EndGame(true, pending);
    }

    private void SendWeaponChoices(PlayerComponent player)
    {
        var choices = _weaponSystem.GenerateChoices(player);
        if (choices.Count == 0) return;

        var noti = new NotiWeaponChoice();
        foreach (var c in choices)
            noti.Choices.Add(new WeaponChoiceInfo
            {
                WeaponId  = c.WeaponId, Name = c.Name,
                NextLevel = c.NextLevel, IsUpgrade = c.IsUpgrade
            });

        _ = player.Session.SendAsync(new GamePacket { NotiWeaponChoice = noti });
    }

    private void CheckAllPlayersDead(List<GamePacket> pending)
    {
        if (_room.GetPlayers().All(p => !p.Character.IsAlive))
            EndGame(false, pending);
    }

    private void EndGame(bool isClear, List<GamePacket> pending)
    {
        if (Interlocked.CompareExchange(ref _endedFlag, 1, 0) != 0) return;

        pending.Add(new GamePacket
        {
            NotiGameEnd = new NotiGameEnd { IsClear = isClear, SurvivedSeconds = (int)_survivalElapsed }
        });
        _cts.Cancel();
        GameSessionRegistry.Instance.Unregister(RoomId);
        GameLogger.Info($"GameSession:{RoomId}", $"게임 종료 (clear={isClear})");
    }

    private static int CalcDamage(int atk, int def) => Math.Max(1, atk - def / 2);

    private static PlayerInfo BuildPlayerInfo(PlayerComponent p) => new()
    {
        PlayerId = p.AccountId, Name = p.Name,
        X = p.World.X, Y = p.World.Y,
        Level = p.Character.Level, Hp = p.Character.Hp, MaxHp = p.Character.MaxHp
    };

    private static MonsterInfo BuildMonsterInfo(MonsterComponent m) => new()
    {
        MonsterId = m.MonsterId, MonsterType = (int)m.Type,
        X = m.X, Y = m.Y, Hp = m.Hp, MaxHp = m.MaxHp
    };
}
