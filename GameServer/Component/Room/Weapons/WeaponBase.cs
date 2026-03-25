namespace GameServer.Component.Room.Weapons;

public enum WeaponId { Garlic = 0, Knife = 1, Axe = 2 }

/// <summary>
/// 서버사이드 자동 무기 기반 클래스.
/// GameSessionComponent._stateLock 하에서 Tick이 호출된다.
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
    public List<(ulong MonsterId, int Damage)> Tick(
        float dt,
        float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters)
    {
        _elapsed += dt;
        if (_elapsed < CooldownSec) return [];

        _elapsed = 0f;
        return TryAttack(ownerX, ownerY, monsters);
    }

    protected abstract List<(ulong MonsterId, int Damage)> TryAttack(
        float ownerX, float ownerY,
        IEnumerable<MonsterComponent> monsters);

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

    protected static float Dist(float ax, float ay, float bx, float by)
        => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    protected static float DistSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }
}
