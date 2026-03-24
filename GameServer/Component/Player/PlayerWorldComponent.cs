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

    public void Move(float x, float y)
    {
        X = Math.Clamp(x, 0f, 800f);
        Y = Math.Clamp(y, 0f, 600f);
    }

    public bool CanAttack()
        => (DateTime.UtcNow - _lastAttackAt).TotalSeconds >= AttackCooldownSec;

    public void ResetAttackCooldown()
        => _lastAttackAt = DateTime.UtcNow;

    protected override void OnDispose() { }
}
