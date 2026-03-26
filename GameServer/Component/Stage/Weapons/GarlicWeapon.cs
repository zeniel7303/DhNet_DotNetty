using GameServer.Component.Stage.Monster;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 마늘 — 지속 오라. 1초마다 반경 내 모든 몬스터에 데미지 + 넉백.
/// 업그레이드 시 반경 및 넉백 거리 확대.
/// </summary>
public class GarlicWeapon : WeaponBase
{
    private float _radius        = 80f;
    private float _knockbackDist = 50f; // 넉백 거리 (픽셀)

    public GarlicWeapon() : base(WeaponId.Garlic)
    {
        Damage      = 5;
        CooldownSec = 1.0f;
    }

    protected override List<WeaponHit> TryAttack(
        float ownerX, float ownerY, IEnumerable<MonsterComponent> monsters)
    {
        var hits    = new List<WeaponHit>();
        float radSq = _radius * _radius;

        foreach (var m in monsters)
        {
            if (!m.IsAlive) continue;

            float dx = m.X - ownerX;
            float dy = m.Y - ownerY;
            float dSq = dx * dx + dy * dy;
            if (dSq > radSq) continue;

            // 넉백 방향: 플레이어 → 몬스터 (반경 내 zero-vector는 랜덤 방향으로 밀어냄)
            float pushX, pushY;
            if (dSq < 1f)
            {
                pushX = _knockbackDist;
                pushY = 0f;
            }
            else
            {
                float dist = MathF.Sqrt(dSq);
                pushX = dx / dist * _knockbackDist;
                pushY = dy / dist * _knockbackDist;
            }

            hits.Add(new WeaponHit(m.MonsterId, Damage, pushX, pushY));
        }
        return hits;
    }

    protected override void OnUpgrade()
    {
        base.OnUpgrade(); // 데미지 +20%, 쿨다운 단축
        _radius += 10f;   // 범위 확대
        // _knockbackDist는 고정 — 레벨업으로 미는 힘은 증가하지 않음
    }
}
