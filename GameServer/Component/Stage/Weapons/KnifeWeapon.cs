using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 단검 — 캐릭터가 바라보는 방향으로 투사체 발사.
/// 비관통 (첫 번째 적중 시 즉시 소멸).
/// WeaponComponent가 매 틱 FacingDirX/Y를 주입한다.
///
/// 투사체 프로토콜 계약:
///   - 수명 만료: NotiProjectileDestroy
///   - 비관통 적중: NotiCombat.projectile_id + NotiProjectileDestroy (즉시 소멸)
/// </summary>
public class KnifeWeapon : WeaponBase
{
    private readonly float _speed;
    private readonly float _lifetime;
    private readonly float _hitRadius;
    private readonly int   _maxProjectiles;

    /// <summary>WeaponComponent가 매 틱 플레이어의 이동 방향으로 갱신한다.</summary>
    public float FacingDirX { get; set; } = 1f;
    public float FacingDirY { get; set; } = 0f;

    private sealed class KnifeProjectile
    {
        public ulong          Id          { get; init; }
        public float          X           { get; set; }
        public float          Y           { get; set; }
        public float          VelX        { get; init; }
        public float          VelY        { get; init; }
        public float          Elapsed     { get; set; }
        public HashSet<ulong> HitMonsters { get; } = new();
    }

    private readonly List<KnifeProjectile> _projectiles    = new();
    private readonly List<GamePacket>      _pendingPackets = new();

    public KnifeWeapon() : base(WeaponId.Knife)
    {
        var stat        = GameDataTable.Weapons[Id.ToString()];
        Damage          = stat.Damage;
        CooldownSec     = stat.CooldownSec;
        _speed          = stat.ProjectileSpeed    ?? 800f;
        _lifetime       = stat.ProjectileLifetime ?? 1.2f;
        _hitRadius      = stat.HitRadius          ?? 28f;
        _maxProjectiles = stat.MaxProjectiles      ?? 15;
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
                if (p.HitMonsters.Contains(m.MonsterId)) continue;
                if (!SweptHit(p.X, p.Y, p.VelX, p.VelY, dt, m, _hitRadius)) continue;

                hits.Add(new WeaponHit(m.MonsterId, Damage, ProjectileId: p.Id));
                p.HitMonsters.Add(m.MonsterId);

                _pendingPackets.Add(new GamePacket
                {
                    NotiProjectileDestroy = new NotiProjectileDestroy { ProjectileId = p.Id }
                });
                _projectiles.RemoveAt(i);
                destroyed = true;
                break;
            }

            if (destroyed) continue;

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
        if (_projectiles.Count >= _maxProjectiles) return [];

        float dirX = FacingDirX, dirY = FacingDirY;
        ulong id = NextProjectileId();

        _projectiles.Add(new KnifeProjectile
        {
            Id   = id,
            X    = ownerX, Y    = ownerY,
            VelX = dirX * _speed, VelY = dirY * _speed,
        });

        _pendingPackets.Add(new GamePacket
        {
            NotiProjectileSpawn = new NotiProjectileSpawn
            {
                ProjectileId = id,
                WeaponId     = (int)WeaponId.Knife,
                X    = ownerX, Y    = ownerY,
                VelX = dirX * _speed, VelY = dirY * _speed,
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
