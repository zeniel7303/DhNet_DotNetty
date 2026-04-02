using GameServer.Component.Stage.Monster;
using GameServer.Component.Stage.Weapons;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Component.Stage;

/// <summary>
/// 전투 연산과 부수 효과(젬 스폰, 골드 지급, 게임 종료 트리거)를 담당한다.
/// StageComponent와 1:1로 생성되며, 단일 틱 스레드에서만 호출된다.
/// </summary>
internal sealed class StageCombatHelper
{
    private readonly Dictionary<ulong, MonsterComponent>  _monsters;
    private readonly Func<float, float, int, Gem>                                    _spawnGem;
    private readonly Func<float, float, float, List<(ulong Id, float X, float Y, int ExpValue)>> _collectNearby;
    private readonly WeaponComponent                                                 _weaponManager;
    private readonly Action<bool, List<GamePacket>>                                  _onEndGame;

    internal StageCombatHelper(
        Dictionary<ulong, MonsterComponent> monsters,
        Func<float, float, int, Gem> spawnGem,
        Func<float, float, float, List<(ulong Id, float X, float Y, int ExpValue)>> collectNearby,
        WeaponComponent weaponManager,
        Action<bool, List<GamePacket>> onEndGame)
    {
        _monsters      = monsters;
        _spawnGem      = spawnGem;
        _collectNearby = collectNearby;
        _weaponManager = weaponManager;
        _onEndGame     = onEndGame;
    }

    // ──────────────────────────────────────────────────────────────
    // 순수 연산 (상태 없음)
    // ──────────────────────────────────────────────────────────────

    internal static int CalcDamage(int atk, int def) => Math.Max(1, atk - def / 2);

    internal static void GiveGold(PlayerComponent player, int amount)
    {
        if (amount <= 0) return;
        player.Character.AddGold(amount);
        _ = player.Session.SendAsync(new GamePacket
        {
            NotiGoldGain = new NotiGoldGain
            {
                PlayerId = player.AccountId, GoldGained = amount, TotalGold = player.Character.Gold
            }
        });
    }

    // ──────────────────────────────────────────────────────────────
    // 상태 변경 포함 (단일 틱 스레드 전용)
    // ──────────────────────────────────────────────────────────────

    /// <summary>몬스터 사망 시 젬을 스폰하고 NotiGemSpawn을 pending에 추가한다.</summary>
    internal void SpawnGem(float x, float y, int expValue, List<GamePacket> pending)
    {
        var gem = _spawnGem(x, y, expValue);
        pending.Add(new GamePacket
        {
            NotiGemSpawn = new NotiGemSpawn { GemId = gem.Id, X = gem.X, Y = gem.Y, ExpValue = gem.ExpValue }
        });
    }

    /// <summary>이동 후 주변 젬을 자동 수집하고 EXP·레벨업·무기선택을 처리한다.</summary>
    internal void CollectGems(PlayerComponent player, List<GamePacket> pending)
    {
        var collected = _collectNearby(player.World.X, player.World.Y, player.Character.ExpRadiusBonus);
        foreach (var (id, _, _, expValue) in collected)
        {
            int expGained = (int)(expValue * player.Character.ExpMultiplier);
            int levelUps  = player.Character.GainExp(expGained);
            pending.Add(new GamePacket
            {
                NotiGemCollect = new NotiGemCollect { GemId = id, PlayerId = player.AccountId }
            });
            _ = player.Session.SendAsync(new GamePacket
            {
                NotiExpGain = new NotiExpGain
                {
                    PlayerId = player.AccountId, ExpGained = expGained,
                    TotalExp = player.Character.Exp, NextLevelExp = player.Character.NextLevelExp
                }
            });
            if (levelUps > 0)
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
                for (int i = 0; i < levelUps; i++)
                    _weaponManager.EnqueueChoice(player);
            }
        }
    }

    /// <summary>모든 플레이어가 사망했으면 게임 종료를 트리거한다.</summary>
    internal void CheckAllPlayersDead(List<GamePacket> pending, IReadOnlyList<PlayerComponent> players)
    {
        if (players.All(p => !p.Character.IsAlive))
            _onEndGame(false, pending);
    }

    /// <summary>
    /// 자동 무기 히트 결과를 적용한다.
    /// 피해 → 넉백 → HP 변경 → 사망(젬·골드·보스 종료) 순으로 처리한다.
    /// attacker는 호출 측에서 캐시한 alivePlayers로 조회해 전달한다.
    /// </summary>
    internal void ApplyWeaponHit(PlayerComponent? attacker, ulong monsterId, int damage, WeaponId weaponId,
        float pushX, float pushY, ulong projectileId, List<GamePacket> pending)
    {
        if (!_monsters.TryGetValue(monsterId, out var monster) || !monster.IsAlive) return;

        bool died = monster.TakeDamage(damage);

        pending.Add(new GamePacket
        {
            NotiCombat = new NotiCombat
            {
                AttackerPlayerId = attacker?.AccountId ?? 0, TargetMonsterId = monsterId,
                Damage = damage, WeaponId = (int)weaponId, ProjectileId = projectileId
            }
        });

        // 넉백 — 사망 전에 위치를 밀어줘야 클라이언트가 올바른 위치에서 사망 처리
        if (!died && (pushX != 0f || pushY != 0f))
        {
            monster.Knockback(pushX, pushY);
            var movePacket = new NotiMonsterMove();
            movePacket.Moves.Add(new MonsterMoveInfo { MonsterId = monsterId, X = monster.X, Y = monster.Y });
            pending.Add(new GamePacket { NotiMonsterMove = movePacket });
        }
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

        if (attacker != null)
            GiveGold(attacker, monster.GoldReward);

        if (monster.IsBoss) _onEndGame(true, pending);
    }
}
