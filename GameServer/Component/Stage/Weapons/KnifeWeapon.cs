namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 단검 — 가장 가까운 적 방향으로 발사, 직선 경로의 모든 몬스터 관통 (Piercing Line).
/// 빠른 쿨다운(0.5s), 낮은 단일 데미지 대신 관통으로 다수 동시 처치.
/// </summary>
public class KnifeWeapon : WeaponBase
{
    private const float MaxRange   = 400f;
    private       float _knifeWidth = 30f; // 직선 판정 폭 — 업그레이드 시 확대

    public KnifeWeapon() : base(WeaponId.Knife)
    {
        Damage      = 10;
        CooldownSec = 0.5f;
    }

    protected override List<WeaponHit> TryAttack(
        float ownerX, float ownerY, IEnumerable<MonsterComponent> monsters)
    {
        var list = monsters.Where(m => m.IsAlive).ToList();

        // 가장 가까운 몬스터 방향으로 투사 방향 결정
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

        // 직선 위 또는 인접(판정 폭 이내)한 모든 몬스터 관통 적중
        var hits = new List<WeaponHit>();
        foreach (var m in list)
        {
            float mx   = m.X - ownerX;
            float my   = m.Y - ownerY;
            float dot  = mx * ux + my * uy;            // 투영 거리 (앞방향=양수)
            if (dot < 0f || dot > MaxRange) continue;  // 뒤쪽 또는 사거리 초과
            float perp = MathF.Abs(mx * uy - my * ux); // 수직 거리
            if (perp <= _knifeWidth)
                hits.Add(new WeaponHit(m.MonsterId, Damage));
        }
        return hits;
    }

    protected override void OnUpgrade()
    {
        base.OnUpgrade();
        _knifeWidth += 5f; // 업그레이드 시 관통 폭 확대
    }
}
