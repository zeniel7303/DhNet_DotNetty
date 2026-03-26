using GameServer.Component.Stage.Monster;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 도끼 — 플레이어 주변을 공전하며 닿는 적에게 지속 데미지.
/// 레벨업:
///   - 홀수 레벨(3,5,7…): 도끼 개수 +1, 각도 균등 재배치
///   - 짝수 레벨(2,4,6…): 데미지 +20%
/// 매 틱 NotiOrbitalWeaponSync로 모든 도끼 Angle 브로드캐스트 (WeaponManager에서 처리).
/// </summary>
public class AxeWeapon : WeaponBase
{
    public  const float OrbitRadius      = 100f;
    private const float HitRadius        = 35f;
    private const float PerEnemyCooldown = 0.5f;
    private const float AngularSpeed     = MathF.PI; // rad/s — 레벨 무관 고정 (예측 가능한 플레이)

    /// <summary>현재 각 도끼의 공전 각도 (radians). WeaponManager가 읽어 NotiOrbitalWeaponSync 생성.</summary>
    public IReadOnlyList<float> Angles => _angles;
    private readonly List<float> _angles = [0f]; // 초기 도끼 1개

    private readonly Dictionary<ulong, float> _enemyCooldowns = new();

    public AxeWeapon() : base(WeaponId.Axe)
    {
        Damage      = 20;
        CooldownSec = 0f;
    }

    public override List<WeaponHit> Tick(
        float dt, float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        // 모든 도끼 각도 진행
        for (int i = 0; i < _angles.Count; i++)
            _angles[i] = (_angles[i] + AngularSpeed * dt) % (2f * MathF.PI);

        float hitRadSq = HitRadius * HitRadius;
        var   hits     = new List<WeaponHit>();

        // 쿨다운 갱신 (순회 중 수정 방지)
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

        // 각 도끼별 충돌 판정
        foreach (var angle in _angles)
        {
            float axeX = ownerX + MathF.Cos(angle) * OrbitRadius;
            float axeY = ownerY + MathF.Sin(angle) * OrbitRadius;

            foreach (var m in monsters)
            {
                if (!m.IsAlive) continue;
                if (_enemyCooldowns.ContainsKey(m.MonsterId)) continue;

                float dx = m.X - axeX, dy = m.Y - axeY;
                if (dx * dx + dy * dy <= hitRadSq)
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
        // 홀수 레벨(3,5,7…): 개수 증가 / 짝수 레벨(2,4,6…): 데미지 증가
        if (Level % 2 == 1) // Level은 OnUpgrade 호출 시점에 이미 증가된 값
            AddAxe();
        else
            Damage = (int)(Damage * 1.2f);
    }

    /// <summary>도끼 1개 추가 후 현재 기준 각도에서 균등 배치.</summary>
    private void AddAxe()
    {
        int   count     = _angles.Count + 1;
        float baseAngle = _angles.Count > 0 ? _angles[0] : 0f;
        float spacing   = 2f * MathF.PI / count;

        _angles.Clear();
        for (int i = 0; i < count; i++)
            _angles.Add((baseAngle + spacing * i) % (2f * MathF.PI));
    }
}
