namespace GameServer.Component.Room.Weapons;

/// <summary>
/// 단검 — 가장 가까운 살아있는 몬스터 단일 공격. 빠른 쿨다운.
/// </summary>
public class KnifeWeapon : WeaponBase
{
    private const float MaxRangeSq = 400f * 400f;

    public KnifeWeapon() : base(WeaponId.Knife)
    {
        Damage      = 10;
        CooldownSec = 0.5f;
    }

    protected override List<(ulong MonsterId, int Damage)> TryAttack(
        float ownerX, float ownerY, IEnumerable<MonsterComponent> monsters)
    {
        MonsterComponent? nearest = null;
        float minDistSq = MaxRangeSq;

        foreach (var m in monsters)
        {
            if (!m.IsAlive) continue;
            float dSq = DistSq(ownerX, ownerY, m.X, m.Y);
            if (dSq < minDistSq) { minDistSq = dSq; nearest = m; }
        }

        if (nearest == null) return [];
        return [(nearest.MonsterId, Damage)];
    }
}
