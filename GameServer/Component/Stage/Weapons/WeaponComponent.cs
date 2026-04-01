using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 플레이어별 무기 목록을 관리하고 서버사이드 자동 공격 틱을 처리한다.
/// Update() 호출 전 Players/Monsters를 설정하면, LastHits/LastPackets로 결과를 읽을 수 있다.
/// StageComponent 단일 틱 스레드에서만 호출된다.
/// </summary>
public class WeaponComponent : BaseComponent
{
    private readonly Dictionary<ulong, List<WeaponBase>> _playerWeapons = new();
    private readonly Dictionary<ulong, int> _pendingLevelUps = new();
    private readonly HashSet<ulong>         _waitingForChoice = new();

    private static readonly (WeaponId Id, string Name)[] WeaponPool =
    [
        (WeaponId.Garlic, "마늘"),
        (WeaponId.Wand,   "마법 지팡이"),
        (WeaponId.Bible,  "성경"),
        (WeaponId.Axe,    "도끼"),
        (WeaponId.Knife,  "단검"),
        (WeaponId.Cross,  "십자가"),
    ];

    private static readonly (int Id, string Name)[] StatUpgradePool =
    [
        ((int)StatUpgradeId.AttackUp,    "공격력 강화"),
        ((int)StatUpgradeId.MaxHpUp,     "체력 강화"),
        ((int)StatUpgradeId.MoveSpeedUp, "이동 속도 강화"),
        ((int)StatUpgradeId.ExpMultiUp,  "경험치 배율 강화"),
        ((int)StatUpgradeId.ExpRadiusUp, "경험치 반경 강화"),
    ];

    // ──────────────────────────────────────────────────────────────
    // Update() 입력 프로퍼티 — 호출 전 StageComponent가 설정한다
    // ──────────────────────────────────────────────────────────────

    /// <summary>Update() 호출 전 이번 틱의 살아있는 플레이어 목록을 설정한다.</summary>
    public IReadOnlyList<PlayerComponent>? Players { get; set; }
    /// <summary>Update() 호출 전 이번 틱의 몬스터 컬렉션을 설정한다.</summary>
    public IEnumerable<MonsterComponent>? Monsters { get; set; }

    // ──────────────────────────────────────────────────────────────
    // Update() 결과 프로퍼티 — 호출 후 StageComponent가 읽는다
    // ──────────────────────────────────────────────────────────────

    private readonly List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId,
        float PushX, float PushY, ulong ProjectileId)> _lastHits = new();
    private readonly List<GamePacket> _lastPackets = new();

    public IReadOnlyList<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId,
        float PushX, float PushY, ulong ProjectileId)> LastHits => _lastHits;
    public IReadOnlyList<GamePacket> LastPackets => _lastPackets;

    // ──────────────────────────────────────────────────────────────
    // BaseComponent 생명주기
    // ──────────────────────────────────────────────────────────────

    public override void Initialize()
    {
        _playerWeapons.Clear();
        _pendingLevelUps.Clear();
        _waitingForChoice.Clear();
    }

    protected override void OnDispose() { }

    /// <summary>
    /// 모든 플레이어의 무기를 틱 처리.
    /// 호출 전 Players, Monsters 설정 필요. 결과는 LastHits, LastPackets로 읽는다.
    /// </summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        _lastHits.Clear();
        _lastPackets.Clear();

        if (Players == null || Monsters == null) return;

        var orbital = new NotiOrbitalWeaponSync();

        foreach (var player in Players)
        {
            if (!player.Character.IsAlive) continue;
            if (!_playerWeapons.TryGetValue(player.AccountId, out var weapons)) continue;

            foreach (var weapon in weapons)
            {
                if (weapon is KnifeWeapon knife)
                {
                    knife.FacingDirX = player.World.FacingDirX;
                    knife.FacingDirY = player.World.FacingDirY;
                }

                var weaponHits = weapon.Tick(dt, player.World.X, player.World.Y, Monsters);
                foreach (var hit in weaponHits)
                    _lastHits.Add((player.AccountId, hit.MonsterId, hit.Damage, weapon.Id, hit.PushX, hit.PushY, hit.ProjectileId));

                foreach (var pkt in weapon.GetPendingPackets(player.AccountId))
                    _lastPackets.Add(pkt);

                if (weapon is BibleWeapon bible)
                    foreach (var angle in bible.Angles)
                        orbital.Orbitals.Add(new OrbitalWeaponInfo
                        {
                            OwnerId  = player.AccountId,
                            WeaponId = (int)WeaponId.Bible,
                            Angle    = angle
                        });
            }
        }

        if (orbital.Orbitals.Count > 0)
            _lastPackets.Add(new GamePacket { NotiOrbitalWeaponSync = orbital });
    }

    // ──────────────────────────────────────────────────────────────
    // 플레이어 등록/해제
    // ──────────────────────────────────────────────────────────────

    /// <summary>플레이어를 무기 시스템에 등록 (기본 무기: 단검).</summary>
    public void Register(PlayerComponent player)
    {
        var weapons = new List<WeaponBase> { new KnifeWeapon() };
        _playerWeapons[player.AccountId] = weapons;
    }

    public void Unregister(ulong accountId)
    {
        _playerWeapons.Remove(accountId);
        _pendingLevelUps.Remove(accountId);
        _waitingForChoice.Remove(accountId);
    }

    /// <summary>게임 세션 종료 시 전체 정리.</summary>
    public void Clear()
    {
        _playerWeapons.Clear();
        _pendingLevelUps.Clear();
        _waitingForChoice.Clear();
    }

    /// <summary>플레이어의 첫 번째(기본) 무기 ID 반환. 무기가 없으면 Knife.</summary>
    public WeaponId GetPrimaryWeaponId(ulong accountId)
        => _playerWeapons.TryGetValue(accountId, out var weapons) && weapons.Count > 0
            ? weapons[0].Id
            : WeaponId.Knife;

    // ──────────────────────────────────────────────────────────────
    // 레벨업 선택지
    // ──────────────────────────────────────────────────────────────

    public List<WeaponChoice> GenerateChoices(PlayerComponent player)
    {
        if (!_playerWeapons.TryGetValue(player.AccountId, out var owned))
            return [];

        var ownedIds = owned.Select(w => w.Id).ToHashSet();
        var pool     = new List<WeaponChoice>();

        foreach (var w in owned)
        {
            var name = WeaponPool.FirstOrDefault(e => e.Id == w.Id).Name ?? w.Id.ToString();
            pool.Add(new WeaponChoice((int)w.Id, name, w.Level + 1, IsUpgrade: true));
        }
        foreach (var (id, name) in WeaponPool)
        {
            if (!ownedIds.Contains(id))
                pool.Add(new WeaponChoice((int)id, name, 1, IsUpgrade: false));
        }

        foreach (var (id, name) in StatUpgradePool)
            pool.Add(new WeaponChoice(id, name, 1, IsUpgrade: false));

        return pool.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
    }

    public void EnqueueChoice(PlayerComponent player)
    {
        var id = player.AccountId;
        if (_waitingForChoice.Contains(id))
        {
            _pendingLevelUps[id] = _pendingLevelUps.GetValueOrDefault(id, 0) + 1;
            return;
        }
        _waitingForChoice.Add(id);
        FlushChoice(player);
    }

    private void FlushChoice(PlayerComponent player)
    {
        var choices = GenerateChoices(player);
        if (choices.Count == 0)
        {
            _waitingForChoice.Remove(player.AccountId);
            return;
        }

        var noti = new NotiWeaponChoice();
        foreach (var c in choices)
            noti.Choices.Add(new WeaponChoiceInfo
            {
                WeaponId  = c.WeaponId, Name      = c.Name,
                NextLevel = c.NextLevel, IsUpgrade = c.IsUpgrade
            });

        _ = player.Session.SendAsync(new GamePacket { NotiWeaponChoice = noti });
    }

    public void ApplyChoice(PlayerComponent player, int choiceId)
    {
        if (choiceId >= 100)
        {
            ApplyStatUpgrade(player, (StatUpgradeId)choiceId);
        }
        else
        {
            if (!_playerWeapons.TryGetValue(player.AccountId, out var owned)) return;

            var wId      = (WeaponId)choiceId;
            var existing = owned.FirstOrDefault(w => w.Id == wId);
            if (existing != null)
            {
                existing.Upgrade();
            }
            else
            {
                WeaponBase? newWeapon = wId switch
                {
                    WeaponId.Garlic => new GarlicWeapon(),
                    WeaponId.Wand   => new WandWeapon(),
                    WeaponId.Bible  => new BibleWeapon(),
                    WeaponId.Axe    => new AxeWeapon(),
                    WeaponId.Knife  => new KnifeWeapon(),
                    WeaponId.Cross  => new CrossWeapon(),
                    _               => null,
                };
                if (newWeapon == null) return;
                owned.Add(newWeapon);
            }
        }

        var id = player.AccountId;
        _waitingForChoice.Remove(id);
        if (_pendingLevelUps.TryGetValue(id, out int pending) && pending > 0)
        {
            _pendingLevelUps[id] = pending - 1;
            _waitingForChoice.Add(id);
            FlushChoice(player);
        }
    }

    private static void ApplyStatUpgrade(PlayerComponent player, StatUpgradeId upgradeId)
    {
        switch (upgradeId)
        {
            case StatUpgradeId.AttackUp:    player.Character.ApplyAttackUp();    break;
            case StatUpgradeId.MaxHpUp:     player.Character.ApplyMaxHpUp();     break;
            case StatUpgradeId.MoveSpeedUp: player.World.IncreaseSpeed(15f);     break;
            case StatUpgradeId.ExpMultiUp:  player.Character.ApplyExpMultiUp();  break;
            case StatUpgradeId.ExpRadiusUp: player.Character.ApplyExpRadiusUp(); break;
        }

        _ = player.Session.SendAsync(new GamePacket
        {
            NotiStatBoost = new NotiStatBoost
            {
                PlayerId  = player.AccountId,
                Attack    = player.Character.Attack,
                MaxHp     = player.Character.MaxHp,
                MoveSpeed = player.World.MoveSpeed,
                ExpMulti  = player.Character.ExpMultiplier,
                ExpRadius = player.Character.ExpRadiusBonus
            }
        });
    }
}

public record WeaponChoice(int WeaponId, string Name, int NextLevel, bool IsUpgrade);
