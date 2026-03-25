namespace GameServer.Component.Room.Weapons;

/// <summary>
/// 도끼 — 가장 가까운 몬스터 단일 고데미지 공격.
/// </summary>
public class AxeWeapon : WeaponBase
{
    private const float MaxRangeSq = 400f * 400f;

    public AxeWeapon() : base(WeaponId.Axe)
    {
        Damage      = 25;
        CooldownSec = 1.5f;
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
