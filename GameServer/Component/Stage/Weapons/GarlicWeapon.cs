using GameServer.Component.Stage.Monster;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 마늘 — 지속 오라. 쿨다운마다 반경 내 모든 몬스터에 데미지 + 넉백.
/// 업그레이드 시 반경 확대.
/// </summary>
public class GarlicWeapon : WeaponBase
{
    private float _radius;
    private float _knockbackDist;

    public float Radius => _radius;

    public GarlicWeapon() : base(WeaponId.Garlic)
    {
        var stat      = GameDataTable.Weapons[Id.ToString()];
        Damage        = stat.Damage;
        CooldownSec   = stat.CooldownSec;
        _radius       = stat.AuraRadius       ?? 80f;
        _knockbackDist = stat.KnockbackDist   ?? 50f;
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
        base.OnUpgrade();
        var stat = GameDataTable.Weapons[Id.ToString()];
        _radius += stat.UpgradeAuraRadius ?? 10f;
    }
}
