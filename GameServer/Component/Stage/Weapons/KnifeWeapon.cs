using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 단검 — 가장 가까운 적 방향으로 투사체 발사.
/// 기본: 비관통 (첫 번째 적중 시 즉시 소멸).
/// _piercing = true 시 관통형 (HitMonsters HashSet으로 동일 적 1회 제한, 수명까지 유지).
///
/// 투사체 프로토콜 계약:
///   - 수명 만료: NotiProjectileDestroy
///   - 비관통 적중: NotiCombat.projectile_id + NotiProjectileDestroy (즉시 소멸)
///   - 관통 적중: NotiCombat.projectile_id만 (투사체는 계속 날아감)
///
/// Tick 순서:
///   1. 기존 투사체 이동 + 충돌 → 이번 틱 신규 투사체는 다음 틱부터 이동
///   2. base.Tick() → _elapsed 진행 + 쿨다운 만료 시 TryAttack
/// </summary>
public class KnifeWeapon : WeaponBase
{
    private const float Speed         = 700f;  // px/s (비관통이므로 빠르게)
    private const float Lifetime      = 1.5f;  // 초
    private const float HitRadius     = 20f;   // 충돌 반경 px
    private const int   MaxProjectiles = 10;   // 동시 투사체 상한

    /// <summary>관통 여부. false(기본) = 첫 적중 시 소멸, true = 수명까지 관통.</summary>
    private bool _piercing = false;

    private static long _projectileIdSeq;
    private static long NextProjectileId() => Interlocked.Increment(ref _projectileIdSeq);

    private sealed class KnifeProjectile
    {
        public long           Id          { get; init; }
        public float          X           { get; set; }
        public float          Y           { get; set; }
        public float          VelX        { get; init; }
        public float          VelY        { get; init; }
        public float          Elapsed     { get; set; }
        public bool           Piercing    { get; init; }
        public HashSet<ulong> HitMonsters { get; } = new(); // 관통 시에만 실제로 사용
    }

    private readonly List<KnifeProjectile> _projectiles    = new();
    private readonly List<GamePacket>      _pendingPackets = new();

    public KnifeWeapon() : base(WeaponId.Knife)
    {
        Damage      = 15;
        CooldownSec = 1.0f;
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
        float hitRadSq = HitRadius * HitRadius;

        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];

            float nx      = p.X + p.VelX * dt;
            float ny      = p.Y + p.VelY * dt;
            float elapsed = p.Elapsed + dt;
            bool  destroyed = false;

            foreach (var m in monsters)
            {
                if (p.HitMonsters.Contains(m.MonsterId)) continue;

                float dx = m.X - nx, dy = m.Y - ny;
                if (dx * dx + dy * dy > hitRadSq) continue;

                hits.Add(new WeaponHit(m.MonsterId, Damage, ProjectileId: (ulong)p.Id));
                p.HitMonsters.Add(m.MonsterId);

                if (!p.Piercing)
                {
                    // 비관통: 첫 적중 즉시 소멸
                    _pendingPackets.Add(new GamePacket
                    {
                        NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = (ulong)p.Id }
                    });
                    _projectiles.RemoveAt(i);
                    destroyed = true;
                    break;
                }
            }

            if (destroyed) continue;

            if (elapsed >= Lifetime)
            {
                _pendingPackets.Add(new GamePacket
                {
                    NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = (ulong)p.Id }
                });
                _projectiles.RemoveAt(i);
            }
            else
            {
                p.X = nx; p.Y = ny; p.Elapsed = elapsed;
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

        float dx  = nearest.X - ownerX;
        float dy  = nearest.Y - ownerY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return [];

        float ux = dx / len, uy = dy / len;
        long  id = NextProjectileId();

        _projectiles.Add(new KnifeProjectile
        {
            Id       = id,
            X        = ownerX, Y = ownerY,
            VelX     = ux * Speed, VelY = uy * Speed,
            Piercing = _piercing,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = (ulong)id,
                WeaponId     = (int)WeaponId.Knife,
                X = ownerX, Y = ownerY,
                VelX = ux * Speed, VelY = uy * Speed
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

    protected override void OnUpgrade()
    {
        base.OnUpgrade(); // 데미지 +20%, 쿨다운 단축
    }
}
