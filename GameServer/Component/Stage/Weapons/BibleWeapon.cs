using GameServer.Component.Stage.Monster;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 성경 — 플레이어 주변을 공전하며 닿는 적에게 지속 데미지.
/// 레벨업:
///   - 홀수 레벨(3,5,7…): 성경 개수 +1, 각도 균등 재배치
///   - 짝수 레벨(2,4,6…): 데미지 +20%
/// 매 틱 NotiOrbitalWeaponSync로 모든 성경 Angle 브로드캐스트 (WeaponManager에서 처리).
/// </summary>
public class BibleWeapon : WeaponBase
{
    public  const float OrbitRadius      = 100f;
    private const float HitRadius        = 35f;
    private const float PerEnemyCooldown = 0.5f;
    private const float AngularSpeed     = MathF.PI; // rad/s — 레벨 무관 고정

    /// <summary>현재 각 성경의 공전 각도 (radians). WeaponManager가 읽어 NotiOrbitalWeaponSync 생성.</summary>
    public IReadOnlyList<float> Angles => _angles;
    private readonly List<float> _angles = [0f]; // 초기 성경 1개

    private readonly Dictionary<ulong, float> _enemyCooldowns = new();

    public BibleWeapon() : base(WeaponId.Bible)
    {
        Damage      = 20;
        CooldownSec = 0f;
    }

    public override List<WeaponHit> Tick(
        float dt, float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        for (int i = 0; i < _angles.Count; i++)
            _angles[i] = (_angles[i] + AngularSpeed * dt) % (2f * MathF.PI);

        var hits = new List<WeaponHit>();

        var expired  = new List<ulong>();
        var toUpdate = new List<(ulong Id, float Elapsed)>();
        foreach (var (id, elapsed) in _enemyCooldowns)
        {
            float next = elapsed + dt;
            if (next >= PerEnemyCooldown) expired.Add(id);
            else                          toUpdate.Add((id, next));
        }
        foreach (var id in expired)        _enemyCooldowns.Remove(id);
        foreach (var (id, e) in toUpdate)  _enemyCooldowns[id] = e;

        foreach (var angle in _angles)
        {
            float bibleX = ownerX + MathF.Cos(angle) * OrbitRadius;
            float bibleY = ownerY + MathF.Sin(angle) * OrbitRadius;

            foreach (var m in monsters)
            {
                if (!m.IsAlive) continue;
                if (_enemyCooldowns.ContainsKey(m.MonsterId)) continue;

                float dx = m.X - bibleX, dy = m.Y - bibleY;
                float combined = HitRadius + m.HitRadius;
                if (dx * dx + dy * dy <= combined * combined)
                {
                    hits.Add(new WeaponHit(m.MonsterId, Damage));
                    _enemyCooldowns[m.MonsterId] = 0f;
                }
            }
        }

        return hits;
    }

    protected override void OnUpgrade()
    {
        if (Level % 2 == 1)
            AddBible();
        else
            Damage = (int)(Damage * 1.2f);
    }

    private void AddBible()
    {
        int   count     = _angles.Count + 1;
        float baseAngle = _angles.Count > 0 ? _angles[0] : 0f;
        float spacing   = 2f * MathF.PI / count;

        _angles.Clear();
        for (int i = 0; i < count; i++)
            _angles.Add((baseAngle + spacing * i) % (2f * MathF.PI));
    }
}
