using System.Collections.Concurrent;
using Common.Logging;
using Common.Server.Component;
using GameServer.Component.Stage.Monster;
using GameServer.Component.Stage.Wave;
using GameServer.Component.Stage.Weapons;
using GameServer.Component.Player;
using GameServer.Component.Room;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Stage;

internal sealed class Gem
{
    public ulong Id       { get; private set; }
    public float X        { get; private set; }
    public float Y        { get; private set; }
    public int   ExpValue { get; private set; }
    internal void Reset(ulong id, float x, float y, int expValue) { Id = id; X = x; Y = y; ExpValue = expValue; }
}

/// <summary>
/// 게임 진행 중 상태를 관리하는 컴포넌트. RoomComponent와 1:1로 생성된다.
/// 게임 틱은 RoomSystem(WorkerSystem)의 RoomComponent.Update(dt) 호출로 구동된다.
///
/// 동시성 모델:
///   - Update() : RoomSystem 워커 스레드 — 단일 스레드 순차 실행 (AI, 웨이브, 입력 드레인)
///   - ProcessXxx() : PlayerComponent 워커 스레드 — _inputQueue에만 적재, lock 없음
///   → ConcurrentQueue로 스레드 안전 보장, 별도 lock 불필요
/// </summary>
public class StageComponent : BaseComponent
{
    private static long _monsterIdSeq;
    private static ulong NextMonsterId() => (ulong)Interlocked.Increment(ref _monsterIdSeq);
    private static long _gemIdSeq;
    private static ulong NextGemId() => (ulong)Interlocked.Increment(ref _gemIdSeq);

    private readonly RoomComponent  _room;
    private readonly Dictionary<ulong, MonsterComponent> _monsters = new();
    private readonly Dictionary<ulong, Gem> _gems    = new();
    private readonly Stack<Gem>             _gemPool = new();
    private readonly WaveComponent    _waveSpawner   = new();
    private readonly WeaponComponent  _weaponManager = new();
    private readonly StageCombatHelper _combat;

    // 플레이어 입력 큐 — 워커 스레드가 lock 없이 적재, Update에서 일괄 처리
    private readonly ConcurrentQueue<Action<List<GamePacket>>> _inputQueue = new();
    private readonly List<GamePacket> _pending = new();  // 매 틱 재사용 — GC 감소

    private int   _endedFlag;
    private int   _cleanupCounter;
    private float _survivalElapsed;
    private float _survivalBroadcastAcc;
    private const float ClearTimeSec = 1800f;

    public ulong RoomId => _room.RoomId;

    public StageComponent(RoomComponent room)
    {
        _room   = room;
        _combat = new StageCombatHelper(_monsters, SpawnGemAt, CollectNearbyGems, _weaponManager, EndGame);
    }

    // 플레이어 스폰 위치 — 맵 중앙 기준 (3200x2400)
    private static readonly (float X, float Y)[] SpawnPoints =
    [
        (1500f, 1200f),
        (1700f, 1200f),
        (1600f, 1100f),
        (1600f, 1300f),
    ];

    // ──────────────────────────────────────────────────────────────
    // BaseComponent 생명주기
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 게임 세션 초기화. 플레이어 배치, 무기 등록, 몬스터 스폰, 초기 상태 전송.
    /// RoomComponent.Ready()에서 GameSessionRegistry 등록 후 호출된다.
    /// </summary>
    public override void Initialize()
    {
        foreach (var gem in _gems.Values) _gemPool.Push(gem);
        _gems.Clear();
        _waveSpawner.Initialize();
        _weaponManager.Initialize();

        var players = _room.GetPlayers();
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
            _weaponManager.Register(p);

        SpawnMonsters();
        SendInitialState(players);
        GameLogger.Info($"GameSession:{RoomId}", $"게임 세션 시작 (플레이어 {players.Count}명, 몬스터 {_monsters.Count}개)");
    }

    protected override void OnDispose()
    {
        _weaponManager.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 초기화 헬퍼
    // ──────────────────────────────────────────────────────────────

    private void SpawnMonsters()
    {
        // 초기 웨이브 — 맵 중앙(1600, 1200) 주변 스폰
        void Add(MonsterComponent m) => _monsters[m.MonsterId] = m;
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Bat,    1200, 900));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Bat,    2000, 900));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Zombie,  900, 1500));
        Add(new MonsterComponent(NextMonsterId(), MonsterType.Zombie, 2300, 1500));
    }

    private void SendInitialState(IReadOnlyList<PlayerComponent> players)
    {
        var res = new ResEnterGame { ErrorCode = ErrorCode.Success };
        foreach (var p in players)
            res.Players.Add(StageBroadcastHelper.BuildPlayerInfo(p));
        foreach (var m in _monsters.Values)
            res.Monsters.Add(StageBroadcastHelper.BuildMonsterInfo(m));
        _room.BroadcastPacket(new GamePacket { ResEnterGame = res });
    }

    // ──────────────────────────────────────────────────────────────
    // 게임 틱 — RoomSystem(WorkerSystem) → RoomComponent.Update(dt) → StageComponent.Update(dt)
    // ──────────────────────────────────────────────────────────────

    /// <summary>게임 틱 처리. RoomComponent.Update(dt)에 의해 호출된다.</summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        if (IsDisposed || _endedFlag == 1) return;

        _pending.Clear();

        // 플레이어 입력 일괄 처리 — EndGame이 입력 중 트리거된 경우 나머지 입력은 폐기
        while (_inputQueue.TryDequeue(out var input))
        {
            if (_endedFlag == 1) break;
            input(_pending);
        }

        // 입력 처리 중 게임이 종료된 경우 AI 틱 스킵
        if (_endedFlag == 1)
        {
            foreach (var pkt in _pending) _room.BroadcastPacket(pkt);
            return;
        }

        // 생존 타이머
        _survivalElapsed      += dt;
        _survivalBroadcastAcc += dt;
        if (_survivalBroadcastAcc >= 10f)
        {
            _survivalBroadcastAcc = 0f;
            _pending.Add(new GamePacket
            {
                NotiSurvivalTime = new NotiSurvivalTime { ElapsedSeconds = (int)_survivalElapsed }
            });
        }

        if (_survivalElapsed >= ClearTimeSec)
        {
            EndGame(true, _pending);
        }
        else
        {
            var players      = _room.GetPlayers();                                   // 1회 캐싱
            var alivePlayers = players.Where(p => p.Character.IsAlive).ToList();
            List<MonsterMoveInfo>? movedList = null;

            // 몬스터 AI 틱
            foreach (var monster in _monsters.Values)
            {
                // 가장 가까운 살아있는 플레이어 탐색 — wrap-aware 최단 경로
                var targetX        = monster.X;
                var targetY        = monster.Y;
                var nearestDistSq  = float.MaxValue;
                PlayerComponent? nearestPlayer = null;

                foreach (var p in alivePlayers)
                {
                    var dx = p.World.X - monster.X;
                    var dy = p.World.Y - monster.Y;
                    if (dx >  1600f) dx -= 3200f; else if (dx < -1600f) dx += 3200f;
                    if (dy >  1200f) dy -= 2400f; else if (dy < -1200f) dy += 2400f;
                    var distSq = dx * dx + dy * dy;
                    if (distSq < nearestDistSq)
                    {
                        nearestDistSq = distSq;
                        nearestPlayer = p;
                        // wrap-aware 오프셋을 몬스터 기준 절대 좌표로 변환
                        targetX = monster.X + dx;
                        targetY = monster.Y + dy;
                    }
                }

                monster.TargetX = targetX;
                monster.TargetY = targetY;
                monster.Update(dt);

                if (monster.WasRespawned)
                {
                    _pending.Add(new GamePacket
                    {
                        NotiRespawn = new NotiRespawn { MonsterId = monster.MonsterId, X = monster.X, Y = monster.Y, Hp = monster.Hp }
                    });
                    continue;
                }

                if (monster.WasMoved)
                {
                    movedList ??= [];
                    movedList.Add(new MonsterMoveInfo { MonsterId = monster.MonsterId, X = monster.X, Y = monster.Y });
                }

                // 공격 — 살아있고, AttackRange 이내이고, 쿨다운 해제됐을 때만
                // ShouldAttack()은 쿨다운을 소모하므로 반드시 거리 판정 뒤에 호출해야 한다
                if (!monster.IsAlive || nearestPlayer == null) continue;
                var attackRangeSq = monster.AttackRange * monster.AttackRange;
                if (nearestDistSq > attackRangeSq) continue;
                if (!monster.ShouldAttack()) continue;

                var damage = StageCombatHelper.CalcDamage(monster.Atk, nearestPlayer.Character.Defense);
                var died   = nearestPlayer.Character.TakeDamage(damage);

                _pending.Add(new GamePacket
                {
                    NotiMonsterAttack = new NotiMonsterAttack
                    {
                        MonsterId = monster.MonsterId, TargetPlayerId = nearestPlayer.AccountId, Damage = damage
                    }
                });
                _pending.Add(new GamePacket
                {
                    NotiHpChange = new NotiHpChange
                    {
                        EntityId = nearestPlayer.AccountId, Hp = nearestPlayer.Character.Hp,
                        MaxHp = nearestPlayer.Character.MaxHp, IsMonster = false
                    }
                });

                if (died)
                {
                    _pending.Add(new GamePacket
                    {
                        NotiDeath = new NotiDeath { EntityId = nearestPlayer.AccountId, IsMonster = false }
                    });
                    _combat.CheckAllPlayersDead(_pending, players);
                }
            }

            // 이동한 몬스터들을 단일 패킷으로 배치
            if (movedList is { Count: > 0 })
            {
                var movePacket = new NotiMonsterMove();
                movePacket.Moves.AddRange(movedList);
                _pending.Add(new GamePacket { NotiMonsterMove = movePacket });
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
            _weaponManager.Players = alivePlayers;
            _weaponManager.Monsters = _monsters.Values;
            _weaponManager.Update(dt);
            foreach (var (attackerId, monsterId, dmg, wid, pushX, pushY, projectileId) in _weaponManager.LastHits)
                _combat.ApplyWeaponHit(
                    alivePlayers.FirstOrDefault(p => p.AccountId == attackerId),
                    monsterId, dmg, wid, pushX, pushY, projectileId, _pending);
            _pending.AddRange(_weaponManager.LastPackets);

            // 웨이브 스포너 틱
            _waveSpawner.MonsterCount = _monsters.Count;
            _waveSpawner.Update(dt);
            if (_waveSpawner.LastSpawns is { Count: > 0 }) DoWaveSpawn(_waveSpawner.LastSpawns, _pending);
        }

        foreach (var pkt in _pending)
            _room.BroadcastPacket(pkt);
    }

    // ──────────────────────────────────────────────────────────────
    // 플레이어 입력 처리 — 워커 스레드에서 호출, _inputQueue에만 적재
    // 실제 처리는 Update() 내부에서 일괄 실행된다.
    // ──────────────────────────────────────────────────────────────

    public void ProcessAttack(PlayerComponent player, ulong monsterId)
    {
        if (_endedFlag == 1) return;
        _inputQueue.Enqueue(pending =>
        {
            if (!player.Character.IsAlive) return;
            if (!player.World.CanAttack()) return;
            if (!_monsters.TryGetValue(monsterId, out var monster) || !monster.IsAlive) return;

            var damage = StageCombatHelper.CalcDamage(player.Character.Attack, monster.Def);
            player.World.ResetAttackCooldown();
            var died = monster.TakeDamage(damage);

            pending.Add(new GamePacket
            {
                NotiCombat = new NotiCombat
                {
                    AttackerPlayerId = player.AccountId, TargetMonsterId = monsterId, Damage = damage,
                    WeaponId = (int)_weaponManager.GetPrimaryWeaponId(player.AccountId)
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
                _combat.SpawnGem(monster.X, monster.Y, monster.ExpReward, pending);
                StageCombatHelper.GiveGold(player, monster.GoldReward);
                if (monster.IsBoss) EndGame(true, pending);
            }
        });
    }

    public void ProcessMove(PlayerComponent player, float x, float y)
    {
        if (_endedFlag == 1) return;
        _inputQueue.Enqueue(pending =>
        {
            if (!player.Character.IsAlive) return;
            player.World.Move(x, y);
            pending.Add(new GamePacket
            {
                NotiMove = new NotiMove { PlayerId = player.AccountId, X = player.World.X, Y = player.World.Y }
            });
            // 이동 후 주변 젬 자동 수집
            _combat.CollectGems(player, pending);
        });
    }

    public void ProcessChooseWeapon(PlayerComponent player, int weaponId)
    {
        if (_endedFlag == 1) return;
        _inputQueue.Enqueue(_ => _weaponManager.ApplyChoice(player, weaponId));
    }

    // ProcessChat은 게임 상태를 수정하지 않으므로 큐 없이 직접 브로드캐스트
    public void ProcessChat(PlayerComponent player, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500) return;
        _room.BroadcastPacket(new GamePacket
        {
            NotiGameChat = new NotiGameChat { PlayerId = player.AccountId, PlayerName = player.Name, Message = message }
        });
    }

    // ──────────────────────────────────────────────────────────────
    // 내부 헬퍼 — Update() 단일 스레드에서 호출된다
    // ──────────────────────────────────────────────────────────────

    private Gem SpawnGemAt(float x, float y, int expValue)
    {
        var gem = _gemPool.Count > 0 ? _gemPool.Pop() : new Gem();
        gem.Reset(NextGemId(), x, y, expValue);
        _gems[gem.Id] = gem;
        return gem;
    }

    private List<(ulong Id, float X, float Y, int ExpValue)> CollectNearbyGems(float x, float y, float radiusBonus)
    {
        const float defaultPickupRadius = 50f;
        float radius   = defaultPickupRadius + radiusBonus;
        float radiusSq = radius * radius;
        var result = new List<(ulong, float, float, int)>();
        foreach (var gem in _gems.Values)
        {
            float dx = gem.X - x;
            float dy = gem.Y - y;
            if (dx * dx + dy * dy <= radiusSq)
                result.Add((gem.Id, gem.X, gem.Y, gem.ExpValue));
        }
        foreach (var (id, _, _, _) in result)
        {
            _gemPool.Push(_gems[id]);
            _gems.Remove(id);
        }
        return result;
    }

    private void DoWaveSpawn(List<(MonsterType Type, float X, float Y)> spawns, List<GamePacket> pending)
    {
        foreach (var (type, x, y) in spawns)
        {
            var m = new MonsterComponent(NextMonsterId(), type, x, y, _waveSpawner.WaveNumber);
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

    private void EndGame(bool isClear, List<GamePacket> pending)
    {
        if (Interlocked.CompareExchange(ref _endedFlag, 1, 0) != 0) return;

        pending.Add(new GamePacket
        {
            NotiGameEnd = new NotiGameEnd { IsClear = isClear, SurvivedSeconds = (int)_survivalElapsed }
        });
        GameSessionRegistry.Instance.Unregister(RoomId);
        GameLogger.Info($"GameSession:{RoomId}", $"게임 종료 (clear={isClear})");
    }

}
