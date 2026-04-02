using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 도끼 — 가장 가까운 적 방향으로 포물선 아크를 그리며 날아가는 관통 투사체.
/// 중력의 영향을 받아 위로 솟구쳤다가 내려오며, 경로 상의 모든 적을 관통.
///
/// 투사체 프로토콜 계약:
///   - 수명 만료: NotiProjectileDestroy
///   - 관통 적중: NotiCombat.projectile_id만 (투사체 계속 날아감)
///
/// 위치 계산 (해석적 공식, 서버·클라이언트 동일):
///   x(t) = x0 + velX * t
///   y(t) = y0 + velY0 * t + 0.5 * Gravity * t²
/// </summary>
public class AxeWeapon : WeaponBase
{
    public  const float Gravity          = 1000f; // px/s² (클라이언트와 동일 값 유지)
    private const float HorizontalSpeed  = 400f;  // px/s
    private const float VerticalSpeed    = 500f;  // px/s (초기 상향 속도)
    private const float Lifetime         = 1.0f;  // 초 — 완전한 포물선 1회
    private const float HitRadius        = 25f;   // px
    private const int   MaxProjectiles   = 5;


    private sealed class AxeProjectile
    {
        public ulong          Id          { get; init; }
        public float          StartX      { get; init; }
        public float          StartY      { get; init; }
        public float          VelX        { get; init; }
        public float          VelY0       { get; init; }
        public float          Elapsed     { get; set; }
        public HashSet<ulong> HitMonsters { get; } = new();
    }

    private readonly List<AxeProjectile> _projectiles    = new();
    private readonly List<GamePacket>    _pendingPackets = new();

    public AxeWeapon() : base(WeaponId.Axe)
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

        MoveProjectiles(dt, monsterList, hits);
        hits.AddRange(base.Tick(dt, ownerX, ownerY, monsterList));

        return hits;
    }

    private void MoveProjectiles(float dt, List<MonsterComponent> monsters, List<WeaponHit> hits)
    {
        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p       = _projectiles[i];
            float t = p.Elapsed + dt;
            // 맵 경계 순환 적용 — 클라이언트 렌더링과 동일한 공식
            var (curX, curY) = WrapPos(
                p.StartX + p.VelX  * t,
                p.StartY + p.VelY0 * t + 0.5f * Gravity * t * t);

            // 관통: 경로 상 미명중 적 모두 체크
            foreach (var m in monsters)
            {
                if (p.HitMonsters.Contains(m.MonsterId)) continue;

                float combined = HitRadius + m.HitRadius;
                if (WrappedDistSq(m.X, m.Y, curX, curY) > combined * combined) continue;

                hits.Add(new WeaponHit(m.MonsterId, Damage, ProjectileId: p.Id));
                p.HitMonsters.Add(m.MonsterId);
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
            float dSq = DistSq(ownerX, ownerY, m.X, m.Y);
            if (dSq < minDistSq) { minDistSq = dSq; nearest = m; }
        }
        if (nearest == null) return [];

        // 가장 가까운 적 방향의 수평 성분만 사용, 수직은 항상 위쪽
        float dirX = nearest.X >= ownerX ? 1f : -1f;
        float velX = dirX * HorizontalSpeed;
        float velY = -VerticalSpeed; // 위로 발사 (Y 축: 아래가 양수)

        ulong id = NextProjectileId();
        _projectiles.Add(new AxeProjectile
        {
            Id     = id,
            StartX = ownerX, StartY = ownerY,
            VelX   = velX,   VelY0  = velY,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = id,
                WeaponId     = (int)WeaponId.Axe,
                X    = ownerX, Y    = ownerY,
                VelX = velX,   VelY = velY,
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
