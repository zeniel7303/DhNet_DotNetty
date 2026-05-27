using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 플레이어별 무기 목록을 관리하고 서버사이드 자동 공격 틱을 처리한다.
/// Update() 호출 전 Players/Monsters를 설정하면, LastHits/LastPackets로 결과를 읽을 수 있다.
/// StageComponent 단일 틱 스레드에서만 호출된다.
/// </summary>
public class WeaponComponent : BaseComponent
{
    private readonly Dictionary<ulong, List<WeaponBase>>      _playerWeapons     = new();
    private readonly Dictionary<ulong, int>                   _pendingLevelUps   = new();
    private readonly HashSet<ulong>                           _waitingForChoice  = new();
    // 스탯 업그레이드 선택 횟수 추적 (accountId → choiceId → 선택 횟수)
    private readonly Dictionary<ulong, Dictionary<int, int>>  _statUpgradeLevels = new();

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
        _statUpgradeLevels.Clear();
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

    /// <summary>플레이어를 무기 시스템에 등록 (기본 무기: 단검). 클라이언트에 초기 무기 목록 전송.</summary>
    public void Register(PlayerComponent player)
    {
        var knife   = new KnifeWeapon();
        var weapons = new List<WeaponBase> { knife };
        _playerWeapons[player.AccountId] = weapons;
        SendWeaponUpgrade(player, knife);
    }

    public void Unregister(ulong accountId)
    {
        _playerWeapons.Remove(accountId);
        _pendingLevelUps.Remove(accountId);
        _waitingForChoice.Remove(accountId);
        _statUpgradeLevels.Remove(accountId);
    }

    /// <summary>게임 세션 종료 시 전체 정리.</summary>
    public void Clear()
    {
        _playerWeapons.Clear();
        _pendingLevelUps.Clear();
        _waitingForChoice.Clear();
        _statUpgradeLevels.Clear();
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

        _statUpgradeLevels.TryGetValue(player.AccountId, out var statLevels);
        foreach (var (id, name) in StatUpgradePool)
        {
            var curLevel = statLevels?.GetValueOrDefault(id, 0) ?? 0;
            pool.Add(new WeaponChoice(id, name, curLevel + 1, IsUpgrade: curLevel > 0));
        }

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
        // 레벨업 선택 대기 중이 아니면 처리 거부 — 악의적 ReqChooseWeapon 스팸 방지
        if (!_waitingForChoice.Contains(player.AccountId)) return;

        if (choiceId >= 100)
        {
            ApplyStatUpgrade(player, (StatUpgradeId)choiceId);
            var accountId = player.AccountId;
            if (!_statUpgradeLevels.TryGetValue(accountId, out var statLevels))
                _statUpgradeLevels[accountId] = statLevels = new Dictionary<int, int>();
            statLevels[choiceId] = statLevels.GetValueOrDefault(choiceId, 0) + 1;
        }
        else
        {
            if (!_playerWeapons.TryGetValue(player.AccountId, out var owned)) return;

            var wId      = (WeaponId)choiceId;
            var existing = owned.FirstOrDefault(w => w.Id == wId);
            if (existing != null)
            {
                existing.Upgrade();
                SendWeaponUpgrade(player, existing);
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
                SendWeaponUpgrade(player, newWeapon);
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
            case StatUpgradeId.MoveSpeedUp: player.World.IncreaseSpeed(GameDataTable.Player.MoveSpeedUpAmount); break;
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
                ExpRadius = player.Character.ExpRadiusBonus,
                CurrentHp = player.Character.Hp,
            }
        });
    }

    private static void SendWeaponUpgrade(PlayerComponent player, WeaponBase weapon)
    {
        var noti = new NotiWeaponUpgrade
        {
            PlayerId = player.AccountId,
            WeaponId = (int)weapon.Id,
            Level    = weapon.Level,
        };
        if (weapon is GarlicWeapon garlic)
            noti.Param1 = garlic.Radius;
        _ = player.Session.SendAsync(new GamePacket { NotiWeaponUpgrade = noti });
    }
}

public record WeaponChoice(int WeaponId, string Name, int NextLevel, bool IsUpgrade);
