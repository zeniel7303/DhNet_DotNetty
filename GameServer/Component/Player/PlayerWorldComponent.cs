using Common.Server.Component;

namespace GameServer.Component.Player;

public class PlayerWorldComponent : BaseComponent
{
    public float X { get; private set; } = 100f;
    public float Y { get; private set; } = 100f;

    private DateTime _lastAttackAt = DateTime.MinValue;
    private const float AttackCooldownSec = 1f;

    public override void Initialize() { }

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
    }

    private const float MapW = 3200f;
    private const float MapH = 2400f;

    public void Move(float x, float y)
    {
        // 맵 순환 — 경계를 넘으면 반대편으로
        X = ((x % MapW) + MapW) % MapW;
        Y = ((y % MapH) + MapH) % MapH;
    }

    public bool CanAttack()
        => (DateTime.UtcNow - _lastAttackAt).TotalSeconds >= AttackCooldownSec;

    public void ResetAttackCooldown()
        => _lastAttackAt = DateTime.UtcNow;

    protected override void OnDispose() { }
}
