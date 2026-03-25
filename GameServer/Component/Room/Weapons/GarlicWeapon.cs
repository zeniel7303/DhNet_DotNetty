namespace GameServer.Component.Room.Weapons;

/// <summary>
/// 마늘 — 플레이어 주변 반경 내 모든 살아있는 몬스터 공격.
/// </summary>
public class GarlicWeapon : WeaponBase
{
    private float _radius = 80f;

    public GarlicWeapon() : base(WeaponId.Garlic)
    {
        Damage      = 15;
        CooldownSec = 3.0f;
    }

    protected override List<(ulong MonsterId, int Damage)> TryAttack(
        float ownerX, float ownerY, IEnumerable<MonsterComponent> monsters)
    {
        var hits     = new List<(ulong, int)>();
        float radSq  = _radius * _radius;
        foreach (var m in monsters)
        {
            if (!m.IsAlive) continue;
            if (DistSq(ownerX, ownerY, m.X, m.Y) <= radSq)
                hits.Add((m.MonsterId, Damage));
        }
        return hits;
    }

    protected override void OnUpgrade()
    {
        base.OnUpgrade();
        _radius += 10f;
    }
}
