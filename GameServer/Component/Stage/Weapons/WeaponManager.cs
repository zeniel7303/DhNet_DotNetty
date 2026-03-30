using Common.Logging;
using GameServer.Component.Player;
using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 플레이어별 무기 목록을 관리하고 서버사이드 자동 공격 틱을 처리한다.
/// GameStage._stateLock 하에서만 호출된다.
/// </summary>
public class WeaponManager
{
    // 플레이어 계정 ID → 보유 무기 목록
    private readonly Dictionary<ulong, List<WeaponBase>> _playerWeapons = new();

    // 레벨업 선택 큐 — 선택 대기 중 추가 레벨업이 발생하면 카운터 증가
    // GameStage._stateLock 하에서만 접근하므로 일반 Dictionary/HashSet 사용
    private readonly Dictionary<ulong, int> _pendingLevelUps = new();
    private readonly HashSet<ulong>         _waitingForChoice = new();

    // 레벨업 시 제시할 무기 선택지 풀
    private static readonly (WeaponId Id, string Name)[] WeaponPool =
    [
        (WeaponId.Garlic, "마늘"),
        (WeaponId.Wand,   "마법 지팡이"),
        (WeaponId.Bible,  "성경"),
        (WeaponId.Axe,    "도끼"),
        (WeaponId.Knife,  "단검"),
        (WeaponId.Cross,  "십자가"),
    ];

    // 레벨업 시 제시할 스탯 업그레이드 선택지 풀 (항상 제공, 중복 선택 가능)
    private static readonly (int Id, string Name)[] StatUpgradePool =
    [
        ((int)StatUpgradeId.AttackUp,    "공격력 강화"),
        ((int)StatUpgradeId.MaxHpUp,     "체력 강화"),
        ((int)StatUpgradeId.MoveSpeedUp, "이동 속도 강화"),
        ((int)StatUpgradeId.ExpMultiUp,  "경험치 배율 강화"),
        ((int)StatUpgradeId.ExpRadiusUp, "경험치 반경 강화"),
    ];

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

    /// <summary>
    /// 레벨업 시 3개의 선택지를 반환한다.
    /// 무기(보유 → 업그레이드, 미보유 → 신규)와 스탯 업그레이드를 합쳐 랜덤 3개 선택.
    /// </summary>
    public List<WeaponChoice> GenerateChoices(PlayerComponent player)
    {
        if (!_playerWeapons.TryGetValue(player.AccountId, out var owned))
            return [];

        var ownedIds = owned.Select(w => w.Id).ToHashSet();
        var pool     = new List<WeaponChoice>();

        // 무기 선택지 (보유 → 업그레이드, 미보유 → 신규)
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

        // 스탯 업그레이드 선택지 (항상 제공)
        foreach (var (id, name) in StatUpgradePool)
            pool.Add(new WeaponChoice(id, name, 1, IsUpgrade: false));

        // 3개 랜덤 선택
        return pool.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
    }

    /// <summary>
    /// 레벨업 무기 선택 요청을 큐에 넣는다.
    /// 이미 선택 대기 중이면 카운터만 증가시키고, 아니면 즉시 선택지를 전송한다.
    /// _stateLock 하에서 호출된다.
    /// </summary>
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
            // 선택지 없음 — 대기 해제 후 종료
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

        player.Session.SendAsync(new GamePacket { NotiWeaponChoice = noti })
            .ContinueWith(t => GameLogger.Error("WeaponManager", "NotiWeaponChoice 전송 실패", t.Exception!.GetBaseException()),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 플레이어가 선택지를 고른 경우 무기 추가/업그레이드 또는 스탯 업그레이드를 적용한다.
    /// 대기 중인 선택지가 있으면 자동으로 다음 선택지를 전송한다.
    /// </summary>
    public void ApplyChoice(PlayerComponent player, int choiceId)
    {
        if (choiceId >= 100)
        {
            // 스탯 업그레이드
            ApplyStatUpgrade(player, (StatUpgradeId)choiceId);
        }
        else
        {
            // 무기 추가 or 업그레이드
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

        // 선택 완료 — 대기 중인 레벨업이 있으면 다음 선택지를 즉시 전송
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

        player.Session.SendAsync(new GamePacket
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
        }).ContinueWith(t => GameLogger.Error("WeaponManager", "NotiStatBoost 전송 실패", t.Exception!.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 모든 플레이어의 무기를 틱 처리.
    /// 반환: Hits — 이번 틱에 발생한 자동공격 목록, Packets — 무기가 생성한 추가 패킷 목록.
    /// </summary>
    public (List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId, float PushX, float PushY, ulong ProjectileId)> Hits,
            List<GamePacket> Packets) Tick(
        float dt,
        IReadOnlyList<PlayerComponent> players,
        IEnumerable<MonsterComponent> monsters)
    {
        var hits    = new List<(ulong, ulong, int, WeaponId, float, float, ulong)>();
        var packets = new List<GamePacket>();
        var orbital = new NotiOrbitalWeaponSync();

        foreach (var player in players)
        {
            if (!player.Character.IsAlive) continue;
            if (!_playerWeapons.TryGetValue(player.AccountId, out var weapons)) continue;

            foreach (var weapon in weapons)
            {
                // 단검: 매 틱 플레이어의 이동 방향을 주입
                if (weapon is KnifeWeapon knife)
                {
                    knife.FacingDirX = player.World.FacingDirX;
                    knife.FacingDirY = player.World.FacingDirY;
                }

                var weaponHits = weapon.Tick(dt, player.World.X, player.World.Y, monsters);
                foreach (var hit in weaponHits)
                    hits.Add((player.AccountId, hit.MonsterId, hit.Damage, weapon.Id, hit.PushX, hit.PushY, hit.ProjectileId));

                // 무기 생성 패킷 수집 — GetPendingPackets 내부에서 OwnerId 주입
                foreach (var pkt in weapon.GetPendingPackets(player.AccountId))
                    packets.Add(pkt);

                // 공전 무기 각도 동기화 — 성경 개수만큼 OrbitalWeaponInfo 생성
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
            packets.Add(new GamePacket { NotiOrbitalWeaponSync = orbital });

        return (hits, packets);
    }
}

public record WeaponChoice(int WeaponId, string Name, int NextLevel, bool IsUpgrade);
