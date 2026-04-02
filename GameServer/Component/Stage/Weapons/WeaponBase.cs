using GameServer.Component.Stage.Monster;
using GameServer.Protocol;
using GameServer.Resources;

namespace GameServer.Component.Stage.Weapons;

public enum WeaponId { Garlic = 0, Wand = 1, Bible = 2, Axe = 3, Knife = 4, Cross = 5 }

/// <summary>무기 1회 적중 결과. PushX/PushY가 0이면 넉백 없음. ProjectileId가 0이면 투사체 없음.</summary>
public readonly record struct WeaponHit(ulong MonsterId, int Damage, float PushX = 0f, float PushY = 0f, ulong ProjectileId = 0);

/// <summary>
/// 서버사이드 자동 무기 기반 클래스.
/// StageComponent.Update() 단일 틱 스레드에서 Tick이 호출된다.
/// </summary>
public abstract class WeaponBase
{
    public WeaponId Id      { get; }
    public int      Level   { get; private set; } = 1;
    public int      Damage  { get; protected set; }
    protected float CooldownSec { get; set; }

    private float _elapsed;

    protected WeaponBase(WeaponId id)
    {
        Id = id;
    }

    /// <summary>
    /// 틱 처리. 쿨다운이 차면 TryAttack을 호출하여 데미지 결과를 반환한다.
    /// 반환: (targetMonsterId, damage)[] — 이번 틱에 공격한 몬스터 목록.
    /// </summary>
    public virtual List<WeaponHit> Tick(
        float dt,
        float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        _elapsed += dt;
        if (_elapsed < CooldownSec) return [];

        _elapsed -= CooldownSec; // 초과분 이월 — 틱 타이밍 오차 방지
        return TryAttack(ownerX, ownerY, monsters);
    }

    /// <summary>
    /// 쿨다운 만료 시 공격을 시도한다. 공전형 무기(AxeWeapon 등)처럼 Tick을 완전히 override하는 경우
    /// 이 메서드는 호출되지 않으며 기본 구현(빈 리스트 반환)을 그대로 사용한다.
    /// </summary>
    protected virtual List<WeaponHit> TryAttack(
        float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters) => [];

    /// <summary>
    /// 이번 틱에 브로드캐스트할 추가 패킷 (투사체 스폰/소멸 등). 기본 빈 리스트.
    /// ownerId: 이 무기를 소지한 플레이어 계정 ID — NotiProjectileSpawn.OwnerId 등 주입에 사용.
    /// </summary>
    public virtual IReadOnlyList<GamePacket> GetPendingPackets(ulong ownerId) => [];

    public void Upgrade()
    {
        Level++;
        OnUpgrade();
    }

    protected virtual void OnUpgrade()
    {
        if (!GameDataTable.Weapons.TryGetValue(Id.ToString(), out var stat))
            throw new InvalidOperationException(
                $"WeaponId '{Id}'을(를) GameDataTable에서 찾을 수 없습니다. weapons.json을 확인하세요.");
        Damage      = (int)(Damage * stat.UpgradeMultDamage);
        CooldownSec = MathF.Max(stat.CooldownMin, CooldownSec * stat.UpgradeMultCooldown);
    }

    // 투사체 위치 계산 시 맵 경계 처리에 사용
    protected const float MapW = 3200f;
    protected const float MapH = 2400f;

    /// <summary>투사체·몬스터 위치를 맵 범위 [0, MapW) × [0, MapH)로 정규화.</summary>
    protected static (float x, float y) WrapPos(float x, float y)
        => (((x % MapW) + MapW) % MapW, ((y % MapH) + MapH) % MapH);

    protected static float DistSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// 맵 경계 순환을 고려한 두 점 사이 거리 제곱.
    /// 투사체 히트 판정에 사용 — 경계 바로 너머 적을 누락하는 오류를 방지한다.
    /// </summary>
    protected static float WrappedDistSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx;
        float dy = ay - by;
        if      (dx >  MapW * 0.5f) dx -= MapW;
        else if (dx < -MapW * 0.5f) dx += MapW;
        if      (dy >  MapH * 0.5f) dy -= MapH;
        else if (dy < -MapH * 0.5f) dy += MapH;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// 모든 투사체 무기가 공유하는 전역 ID 시퀀스.
    /// 무기별 독립 시퀀스를 사용하면 서로 다른 무기가 동일 ID를 발급할 수 있으므로
    /// 반드시 이 메서드를 통해 ID를 발급한다.
    /// </summary>
    private static long _projectileIdSeq;
    protected static ulong NextProjectileId() => (ulong)Interlocked.Increment(ref _projectileIdSeq);
}
