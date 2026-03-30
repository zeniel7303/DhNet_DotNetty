using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 마법 지팡이 — 가장 가까운 적을 자동으로 조준하여 마법 투사체 발사.
/// 비관통 (첫 번째 적중 시 즉시 소멸).
///
/// 투사체 프로토콜 계약:
///   - 수명 만료: NotiProjectileDestroy
///   - 비관통 적중: NotiCombat.projectile_id + NotiProjectileDestroy (즉시 소멸)
/// </summary>
public class WandWeapon : WeaponBase
{
    private const float Speed          = 700f;
    private const float Lifetime       = 1.5f;
    private const float HitRadius      = 20f;
    private const int   MaxProjectiles = 10;

    private sealed class WandProjectile
    {
        public ulong          Id          { get; init; }
        public float          X           { get; set; }
        public float          Y           { get; set; }
        public float          VelX        { get; init; }
        public float          VelY        { get; init; }
        public float          Elapsed     { get; set; }
        public HashSet<ulong> HitMonsters { get; } = new();
    }

    private readonly List<WandProjectile> _projectiles    = new();
    private readonly List<GamePacket>     _pendingPackets = new();

    public WandWeapon() : base(WeaponId.Wand)
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

            float elapsed = p.Elapsed + dt;
            bool  destroyed = false;

            // 맵 경계 순환 적용 — 클라이언트 렌더링과 동일한 공식
            var (nx, ny) = WrapPos(p.X + p.VelX * dt, p.Y + p.VelY * dt);

            foreach (var m in monsters)
            {
                if (p.HitMonsters.Contains(m.MonsterId)) continue;

                if (WrappedDistSq(m.X, m.Y, nx, ny) > hitRadSq) continue;

                hits.Add(new WeaponHit(m.MonsterId, Damage, ProjectileId: p.Id));
                p.HitMonsters.Add(m.MonsterId);

                // 비관통: 첫 적중 즉시 소멸
                _pendingPackets.Add(new GamePacket
                {
                    NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = p.Id }
                });
                _projectiles.RemoveAt(i);
                destroyed = true;
                break;
            }

            if (destroyed) continue;

            if (elapsed >= Lifetime)
            {
                _pendingPackets.Add(new GamePacket
                {
                    NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = p.Id }
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

        _projectiles.Add(new WandProjectile
        {
            Id   = id,
            X    = ownerX, Y    = ownerY,
            VelX = ux * Speed, VelY = uy * Speed,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = id,
                WeaponId     = (int)WeaponId.Wand,
                X = ownerX, Y = ownerY,
                VelX = ux * Speed, VelY = uy * Speed,
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
