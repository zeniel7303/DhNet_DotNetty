using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage.Weapons;

public enum WeaponId { Garlic = 0, Knife = 1, Axe = 2 }

/// <summary>무기 1회 적중 결과. PushX/PushY가 0이면 넉백 없음. ProjectileId가 0이면 투사체 없음.</summary>
public readonly record struct WeaponHit(ulong MonsterId, int Damage, float PushX = 0f, float PushY = 0f, ulong ProjectileId = 0);

/// <summary>
/// 서버사이드 자동 무기 기반 클래스.
/// GameStage._stateLock 하에서 Tick이 호출된다.
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
        Damage      = (int)(Damage * 1.2f);
        CooldownSec = MathF.Max(0.3f, CooldownSec * 0.9f);
    }

    protected static float DistSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }
}
