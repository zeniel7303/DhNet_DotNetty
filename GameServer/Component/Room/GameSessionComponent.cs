using Common.Logging;
using GameServer.Component.Player;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Room;

/// <summary>
/// 게임 진행 중 상태를 관리하는 컴포넌트. RoomComponent와 1:1로 생성된다.
/// 몬스터 AI 틱은 내부 PeriodicTimer(500ms)로 구동된다.
/// </summary>
public class GameSessionComponent
{
    private static long _monsterIdSeq;
    private static ulong NextMonsterId() => (ulong)Interlocked.Increment(ref _monsterIdSeq);

    private readonly RoomComponent _room;
    private readonly List<MonsterComponent> _monsters = new();
    private readonly object _stateLock = new();
    private int _endedFlag;

    public ulong RoomId => _room.RoomId;

    private CancellationTokenSource _cts = new();

    public GameSessionComponent(RoomComponent room)
    {
        _room = room;
    }

    // 플레이어 스폰 위치 — 2인 기준 좌/우 대칭
    private static readonly (float X, float Y)[] SpawnPoints =
    [
        (150f, 300f),
        (650f, 300f),
    ];

    public void Start(IReadOnlyList<PlayerComponent> players)
    {
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            (float X, float Y) spawn = i < SpawnPoints.Length ? SpawnPoints[i] : (400f, 300f);
            p.Character.RestoreFullHp();
            p.World.SetPosition(spawn.X, spawn.Y);
        }

        SpawnMonsters();
        SendInitialState(players);
        _ = RunTickAsync(_cts.Token);
        GameLogger.Info($"GameSession:{RoomId}", $"게임 세션 시작 (플레이어 {players.Count}명, 몬스터 {_monsters.Count}개)");
    }

    private void SpawnMonsters()
    {
        _monsters.Add(new MonsterComponent(NextMonsterId(), MonsterType.Slime,  200, 150));
        _monsters.Add(new MonsterComponent(NextMonsterId(), MonsterType.Slime,  600, 150));
        _monsters.Add(new MonsterComponent(NextMonsterId(), MonsterType.Orc,    400, 350));
        _monsters.Add(new MonsterComponent(NextMonsterId(), MonsterType.Dragon, 400, 500));
    }

    private void SendInitialState(IReadOnlyList<PlayerComponent> players)
    {
        var res = new ResEnterGame { ErrorCode = ErrorCode.Success };

        foreach (var p in players)
            res.Players.Add(BuildPlayerInfo(p));

        foreach (var m in _monsters)
            res.Monsters.Add(BuildMonsterInfo(m));

        _room.BroadcastPacket(new GamePacket { ResEnterGame = res });
    }

    private async Task RunTickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
                Tick(0.5f);
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

        lock (_stateLock)
        {
            foreach (var monster in _monsters)
            {
                bool respawned = monster.Tick(dt);
                if (respawned)
                {
                    _room.BroadcastPacket(new GamePacket
                    {
                        NotiRespawn = new NotiRespawn { MonsterId = monster.MonsterId, X = monster.X, Y = monster.Y, Hp = monster.Hp }
                    });
                    continue;
                }

                if (!monster.IsAlive || !monster.ShouldAttack()) continue;

                var targets = _room.GetPlayers().Where(p => p.Character.IsAlive).ToList();
                if (targets.Count == 0) continue;

                var target = targets[Random.Shared.Next(targets.Count)];
                int damage = CalcDamage(monster.Atk, target.Character.Defense);
                bool died  = target.Character.TakeDamage(damage);

                _room.BroadcastPacket(new GamePacket
                {
                    NotiMonsterAttack = new NotiMonsterAttack
                    {
                        MonsterId = monster.MonsterId, TargetPlayerId = target.AccountId, Damage = damage
                    }
                });
                _room.BroadcastPacket(new GamePacket
                {
                    NotiHpChange = new NotiHpChange
                    {
                        EntityId = target.AccountId, Hp = target.Character.Hp, MaxHp = target.Character.MaxHp, IsMonster = false
                    }
                });

                if (died)
                {
                    _room.BroadcastPacket(new GamePacket
                    {
                        NotiDeath = new NotiDeath { EntityId = target.AccountId, IsMonster = false }
                    });
                    CheckAllPlayersDead();
                }
            }
        }
    }

    // PlayerRpgController → PlayerComponent 워커 스레드에서 호출 (_stateLock으로 Tick과 직렬화)
    public void ProcessAttack(PlayerComponent player, ulong monsterId)
    {
        if (_endedFlag == 1) return;

        lock (_stateLock)
        {
            if (!player.Character.IsAlive) return;
            if (!player.World.CanAttack()) return;

            var monster = _monsters.FirstOrDefault(m => m.MonsterId == monsterId);
            if (monster == null || !monster.IsAlive) return;

            int damage = CalcDamage(player.Character.Attack, monster.Def);
            player.World.ResetAttackCooldown();
            bool died = monster.TakeDamage(damage);

            _room.BroadcastPacket(new GamePacket
            {
                NotiCombat = new NotiCombat
                {
                    AttackerPlayerId = player.AccountId, TargetMonsterId = monsterId, Damage = damage
                }
            });
            _room.BroadcastPacket(new GamePacket
            {
                NotiHpChange = new NotiHpChange
                {
                    EntityId = monsterId, Hp = monster.Hp, MaxHp = monster.MaxHp, IsMonster = true
                }
            });

            if (!died) return;

            _room.BroadcastPacket(new GamePacket
            {
                NotiDeath = new NotiDeath { EntityId = monsterId, IsMonster = true }
            });

            DistributeExp(monster.ExpReward);

            if (monster.IsBoss) EndGame(true);
        }
    }

    public void ProcessMove(PlayerComponent player, float x, float y)
    {
        if (_endedFlag == 1 || !player.Character.IsAlive) return;
        player.World.Move(x, y);
        _room.BroadcastPacket(new GamePacket
        {
            NotiMove = new NotiMove { PlayerId = player.AccountId, X = player.World.X, Y = player.World.Y }
        });
    }

    public void ProcessChat(PlayerComponent player, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500) return;
        _room.BroadcastPacket(new GamePacket
        {
            NotiGameChat = new NotiGameChat { PlayerId = player.AccountId, PlayerName = player.Name, Message = message }
        });
    }

    private void DistributeExp(int expReward)
    {
        foreach (var p in _room.GetPlayers().Where(p => p.Character.IsAlive))
        {
            bool leveled = p.Character.GainExp(expReward);
            _ = p.Session.SendAsync(new GamePacket
            {
                NotiExpGain = new NotiExpGain
                {
                    PlayerId = p.AccountId, ExpGained = expReward,
                    TotalExp = p.Character.Exp, NextLevelExp = p.Character.NextLevelExp
                }
            });
            if (leveled)
            {
                _room.BroadcastPacket(new GamePacket
                {
                    NotiLevelUp = new NotiLevelUp
                    {
                        PlayerId  = p.AccountId, NewLevel  = p.Character.Level,
                        NewMaxHp  = p.Character.MaxHp, NewAttack = p.Character.Attack,
                        NewDefense = p.Character.Defense
                    }
                });
            }
        }
    }

    private void CheckAllPlayersDead()
    {
        if (_room.GetPlayers().All(p => !p.Character.IsAlive))
            EndGame(false);
    }

    private void EndGame(bool isClear)
    {
        if (Interlocked.CompareExchange(ref _endedFlag, 1, 0) != 0) return;

        _room.BroadcastPacket(new GamePacket { NotiGameEnd = new NotiGameEnd { IsClear = isClear } });
        _cts.Cancel();
        MonsterSystem.Instance.Unregister(RoomId);
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
