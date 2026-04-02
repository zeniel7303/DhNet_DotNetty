using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 십자가 — 부메랑 궤적으로 날아갔다가 돌아오는 관통 투사체.
///
/// 궤적 공식 (서버·클라이언트 동일):
///   전진 페이즈 (0 ≤ t &lt; Lifetime/2):
///     pos(t) = Start + Dir * sin(π * t / Lifetime)
///   귀환 페이즈 (Lifetime/2 ≤ t &lt; Lifetime):
///     progress = (t - Lifetime/2) / (Lifetime/2)
///     pos(t) = Peak + (ownerPos - Peak) * progress
///     Peak = Start + Dir  (sin(π/2) = 1)
///     → 플레이어 현재 위치로 선형 귀환 (이동 중 추적)
///
/// 히트 판정 페이즈:
///   - 전진(t &lt; Lifetime/2): HitMonstersFwd로 중복 제한
///   - 귀환(t ≥ Lifetime/2): HitMonstersRet로 중복 제한
///   → 같은 적을 전진·귀환 각 1회씩 총 2회 명중 가능
///
/// NotiProjectileSpawn 인코딩:
///   VelX = DirX * MaxDist, VelY = DirY * MaxDist
/// </summary>
public class CrossWeapon : WeaponBase
{
    public  const float Lifetime       = 1.4f;  // 왕복 시간 초 — 클라이언트 CROSS_LIFETIME과 일치
    private const float MaxDist        = 300f;  // 최대 사거리 px
    private const float HitRadius      = 22f;
    private const int   MaxProjectiles = 3;

    private sealed class CrossProjectile
    {
        public ulong          Id              { get; init; }
        public float          StartX          { get; init; }
        public float          StartY          { get; init; }
        public float          DirX            { get; init; } // 정규화된 방향 * MaxDist
        public float          DirY            { get; init; }
        public float          Elapsed         { get; set; }
        public HashSet<ulong> HitMonstersFwd  { get; } = new(); // 전진 페이즈 명중
        public HashSet<ulong> HitMonstersRet  { get; } = new(); // 귀환 페이즈 명중
    }

    private readonly List<CrossProjectile> _projectiles    = new();
    private readonly List<GamePacket>      _pendingPackets = new();

    public CrossWeapon() : base(WeaponId.Cross)
    {
        var stat    = GameDataTable.Weapons[Id.ToString()];
        Damage      = stat.Damage;
        CooldownSec = stat.CooldownSec;
    }

    public override List<WeaponHit> Tick(
        float dt, float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        _pendingPackets.Clear();
        var monsterList = monsters.Where(m => m.IsAlive).ToList();
        var hits        = new List<WeaponHit>();

        MoveProjectiles(dt, ownerX, ownerY, monsterList, hits);
        hits.AddRange(base.Tick(dt, ownerX, ownerY, monsterList));

        return hits;
    }

    private void MoveProjectiles(float dt, float ownerX, float ownerY, List<MonsterComponent> monsters, List<WeaponHit> hits)
    {
        float halfLife   = Lifetime * 0.5f;

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var   p      = _projectiles[i];
            float t      = p.Elapsed + dt;
            bool  isBack = t >= halfLife;

            float curX, curY;
            if (!isBack)
            {
                // 전진 페이즈: sin 공식 + 맵 경계 순환
                float sinVal = MathF.Sin(MathF.PI * t / Lifetime);
                (curX, curY) = WrapPos(p.StartX + p.DirX * sinVal, p.StartY + p.DirY * sinVal);
            }
            else
            {
                // 귀환 페이즈: 정점 → 현재 플레이어 위치로 선형 보간 + 맵 경계 순환
                float peakX    = p.StartX + p.DirX; // sin(π/2) = 1
                float peakY    = p.StartY + p.DirY;
                float progress = MathF.Min(1f, (t - halfLife) / halfLife);
                (curX, curY) = WrapPos(
                    peakX + (ownerX - peakX) * progress,
                    peakY + (ownerY - peakY) * progress);
            }

            var hitSet = isBack ? p.HitMonstersRet : p.HitMonstersFwd;

            foreach (var m in monsters)
            {
                if (hitSet.Contains(m.MonsterId)) continue;

                float combined = HitRadius + m.HitRadius;
                if (WrappedDistSq(m.X, m.Y, curX, curY) > combined * combined) continue;

                hits.Add(new WeaponHit(m.MonsterId, Damage, ProjectileId: p.Id));
                hitSet.Add(m.MonsterId);
            }

            if (t >= Lifetime)
            {
                _pendingPackets.Add(new GamePacket
                {
                    NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = p.Id }
                });
                _projectiles.RemoveAt(i);
            }
            else
            {
                p.Elapsed = t;
            }
        }
    }

    protected override List<WeaponHit> TryAttack(
        float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        if (_projectiles.Count >= MaxProjectiles) return [];

        MonsterComponent? nearest   = null;
        float             minDistSq = float.MaxValue;
        foreach (var m in monsters)
        {
            float dSq = WrappedDistSq(ownerX, ownerY, m.X, m.Y);
            if (dSq < minDistSq) { minDistSq = dSq; nearest = m; }
        }
        if (nearest == null) return [];

        float dx  = nearest.X - ownerX;
        float dy  = nearest.Y - ownerY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return [];

        float ux = dx / len, uy = dy / len;
        ulong id = NextProjectileId();

        _projectiles.Add(new CrossProjectile
        {
            Id     = id,
            StartX = ownerX, StartY = ownerY,
            DirX   = ux * MaxDist, DirY = uy * MaxDist,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = id,
                WeaponId     = (int)WeaponId.Cross,
                X    = ownerX,         Y    = ownerY,
                VelX = ux * MaxDist,   VelY = uy * MaxDist, // DirX/DirY 인코딩
            }
        });

        return [];
    }

    public override IReadOnlyList<GamePacket> GetPendingPackets(ulong ownerId)
    {
        foreach (var pkt in _pendingPackets)
            if (pkt.NotiProjectileSpawn != null)
                pkt.NotiProjectileSpawn.OwnerId = ownerId;
        return _pendingPackets;
    }

}
