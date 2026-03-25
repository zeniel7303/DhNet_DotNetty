namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 도끼 — 가장 가까운 적 방향 기준 ±ArcHalfAngle° 부채꼴 내 모든 몬스터 고데미지 (Wide Arc).
/// 느린 쿨다운(2s), 높은 데미지, 전방 광범위 타격.
/// </summary>
public class AxeWeapon : WeaponBase
{
    private const float MaxRange = 300f;
    // cos(60°) = 0.5 — 좌우 60° 즉 총 120° 부채꼴
    private float _cosHalfArc = 0.5f;

    public AxeWeapon() : base(WeaponId.Axe)
    {
        Damage      = 25;
        CooldownSec = 2.0f;
    }

    protected override List<WeaponHit> TryAttack(
        float ownerX, float ownerY, IEnumerable<MonsterComponent> monsters)
    {
        var list = monsters.Where(m => m.IsAlive).ToList();

        // 가장 가까운 몬스터 방향을 부채꼴 중심 방향으로 결정
        MonsterComponent? nearest = null;
        float minDistSq = float.MaxValue;
        foreach (var m in list)
        {
            float dSq = DistSq(ownerX, ownerY, m.X, m.Y);
            if (dSq < minDistSq) { minDistSq = dSq; nearest = m; }
        }
        if (nearest == null) return [];

        float dx  = nearest.X - ownerX;
        float dy  = nearest.Y - ownerY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return [new WeaponHit(nearest.MonsterId, Damage)]; // zero-vector guard

        float ux = dx / len; // 정규화 방향 벡터
        float uy = dy / len;

        // 부채꼴 내 모든 몬스터 적중
        var hits = new List<WeaponHit>();
        foreach (var m in list)
        {
            float mx   = m.X - ownerX;
            float my   = m.Y - ownerY;
            float dist = MathF.Sqrt(mx * mx + my * my);
            if (dist > MaxRange) continue;
            if (dist < 1f) { hits.Add(new WeaponHit(m.MonsterId, Damage)); continue; }

            float cosAngle = (mx * ux + my * uy) / dist;
            if (cosAngle >= _cosHalfArc)
                hits.Add(new WeaponHit(m.MonsterId, Damage));
        }
        return hits;
    }

    protected override void OnUpgrade()
    {
        base.OnUpgrade();
        // 업그레이드 시 부채꼴 각도 확대 (cos 값 감소 → 더 넓은 각도)
        // cos(60°)=0.5 → cos(50°)≈0.643 방향이 아니라 각도가 커지므로 cos 값을 줄임
        _cosHalfArc = MathF.Max(0f, _cosHalfArc - 0.1f); // 최대 cos(0°)=1 → cos(90°)=0까지
    }
}
