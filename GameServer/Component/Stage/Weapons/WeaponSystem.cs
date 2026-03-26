using GameServer.Component.Player;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 플레이어별 무기 목록을 관리하고 서버사이드 자동 공격 틱을 처리한다.
/// GameStage._stateLock 하에서만 호출된다.
/// </summary>
public class WeaponSystem
{
    // 플레이어 계정 ID → 보유 무기 목록
    private readonly Dictionary<ulong, List<WeaponBase>> _playerWeapons = new();

    // 레벨업 시 제시할 선택지 풀 (WeaponId, 표시명)
    private static readonly (WeaponId Id, string Name)[] WeaponPool =
    [
        (WeaponId.Garlic, "마늘"),
        (WeaponId.Knife,  "단검"),
        (WeaponId.Axe,    "도끼"),
    ];

    /// <summary>플레이어를 무기 시스템에 등록 (기본 무기: 단검).</summary>
    public void Register(PlayerComponent player)
    {
        var weapons = new List<WeaponBase> { new KnifeWeapon() };
        _playerWeapons[player.AccountId] = weapons;
    }

    public void Unregister(ulong accountId) => _playerWeapons.Remove(accountId);

    /// <summary>
    /// 레벨업 시 3개의 선택지를 반환한다.
    /// 이미 보유한 무기가 있으면 업그레이드 선택지, 없으면 신규 추가.
    /// </summary>
    public List<WeaponChoice> GenerateChoices(PlayerComponent player)
    {
        if (!_playerWeapons.TryGetValue(player.AccountId, out var owned))
            return [];

        var ownedIds  = owned.Select(w => w.Id).ToHashSet();
        var choices   = new List<WeaponChoice>();

        // 보유 무기 업그레이드 선택지 먼저
        foreach (var w in owned)
            choices.Add(new WeaponChoice((int)w.Id, WeaponPool[(int)w.Id].Name, w.Level + 1, IsUpgrade: true));

        // 미보유 신규 무기 선택지
        foreach (var (id, name) in WeaponPool)
        {
            if (!ownedIds.Contains(id))
                choices.Add(new WeaponChoice((int)id, name, 1, IsUpgrade: false));
        }

        // 3개만 랜덤 선택
        return choices.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
    }

    /// <summary>플레이어가 선택지를 고른 경우 무기 추가 or 업그레이드.</summary>
    public void ApplyChoice(PlayerComponent player, int weaponId)
    {
        if (!_playerWeapons.TryGetValue(player.AccountId, out var owned)) return;

        var wId    = (WeaponId)weaponId;
        var existing = owned.FirstOrDefault(w => w.Id == wId);
        if (existing != null)
        {
            existing.Upgrade();
            return;
        }

        WeaponBase newWeapon = wId switch
        {
            WeaponId.Garlic => new GarlicWeapon(),
            WeaponId.Knife  => new KnifeWeapon(),
            WeaponId.Axe    => new AxeWeapon(),
            _               => new GarlicWeapon(),
        };
        owned.Add(newWeapon);
    }

    /// <summary>
    /// 모든 플레이어의 무기를 틱 처리.
    /// 반환: (attackerAccountId, targetMonsterId, damage)[] — 이번 틱에 발생한 자동공격.
    /// </summary>
    public List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId, float PushX, float PushY)> Tick(
        float dt,
        IReadOnlyList<PlayerComponent> players,
        IEnumerable<MonsterComponent> monsters)
    {
        var results = new List<(ulong, ulong, int, WeaponId, float, float)>();

        foreach (var player in players)
        {
            if (!player.Character.IsAlive) continue;
            if (!_playerWeapons.TryGetValue(player.AccountId, out var weapons)) continue;

            foreach (var weapon in weapons)
            {
                var hits = weapon.Tick(dt, player.World.X, player.World.Y, monsters);
                foreach (var hit in hits)
                    results.Add((player.AccountId, hit.MonsterId, hit.Damage, weapon.Id, hit.PushX, hit.PushY));
            }
        }

        return results;
    }
}

public record WeaponChoice(int WeaponId, string Name, int NextLevel, bool IsUpgrade);
