using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

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
    private readonly float _speed;
    private readonly float _lifetime;
    private readonly float _hitRadius;
    private readonly int   _maxProjectiles;

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
        var stat        = GameDataTable.Weapons[Id.ToString()];
        Damage          = stat.Damage;
        CooldownSec     = stat.CooldownSec;
        _speed          = stat.ProjectileSpeed    ?? 700f;
        _lifetime       = stat.ProjectileLifetime ?? 1.5f;
        _hitRadius      = stat.HitRadius          ?? 20f;
        _maxProjectiles = stat.MaxProjectiles      ?? 10;
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
            var p = _projectiles[i];

            float elapsed = p.Elapsed + dt;
            bool  destroyed = false;

            // 맵 경계 순환 적용 — 클라이언트 렌더링과 동일한 공식
            var (nx, ny) = WrapPos(p.X + p.VelX * dt, p.Y + p.VelY * dt);

            foreach (var m in monsters)
            {
                if (p.HitMonsters.Contains(m.MonsterId))
                {
                    continue;
                }
                if (!SweptHit(p.X, p.Y, p.VelX, p.VelY, dt, m, _hitRadius))
                {
                    continue;
                }

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

            if (destroyed)
            {
                continue;
            }

            if (elapsed >= _lifetime)
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
        if (_projectiles.Count >= _maxProjectiles)
        {
            return [];
        }

        MonsterComponent? nearest   = null;
        float             minDistSq = float.MaxValue;
        foreach (var m in monsters)
        {
            float dSq = WrappedDistSq(ownerX, ownerY, m.X, m.Y);
            if (dSq < minDistSq)
            {
                minDistSq = dSq;
                nearest = m;
            }
        }
        if (nearest == null)
        {
            return [];
        }

        float dx  = nearest.X - ownerX;
        float dy  = nearest.Y - ownerY;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f)
        {
            return [];
        }

        float ux = dx / len, uy = dy / len;
        ulong id = NextProjectileId();

        _projectiles.Add(new WandProjectile
        {
            Id   = id,
            X    = ownerX, Y    = ownerY,
            VelX = ux * _speed, VelY = uy * _speed,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = id,
                WeaponId     = (int)WeaponId.Wand,
                X = ownerX, Y = ownerY,
                VelX = ux * _speed, VelY = uy * _speed,
            }
        });

        return [];
    }

    public override IReadOnlyList<GamePacket> GetPendingPackets(ulong ownerId)
    {
        foreach (var pkt in _pendingPackets)
        {
            if (pkt.NotiProjectileSpawn != null)
            {
                pkt.NotiProjectileSpawn.OwnerId = ownerId;
            }
        }
        return _pendingPackets;
    }
}
