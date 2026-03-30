namespace GameServer.Component.Player;

public class PlayerWorldComponent
{
    public float X { get; private set; } = 100f;
    public float Y { get; private set; } = 100f;

    // 기본 이동속도 200 (클라이언트 speed=5 × 60fps ≈ 300px/s 기준)
    public float MoveSpeed { get; private set; } = 200f;

    /// <summary>캐릭터가 마지막으로 이동한 방향 (정규화). 기본값: 오른쪽 (1, 0).</summary>
    public float FacingDirX { get; private set; } = 1f;
    public float FacingDirY { get; private set; } = 0f;

    private DateTime _lastAttackAt = DateTime.MinValue;
    private const float AttackCooldownSec = 0.3f;

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
    }

    private const float MapW = 3200f;
    private const float MapH = 2400f;

    public void Move(float x, float y)
    {
        // 이동 방향 추적 — 맵 순환 경계 점프는 무시
        float dx = x - X;
        float dy = y - Y;
        // 맵 절반 이상 이동하면 순환 점프로 간주하여 방향 갱신 안 함
        if (MathF.Abs(dx) < MapW * 0.5f && MathF.Abs(dy) < MapH * 0.5f)
        {
            float lenSq = dx * dx + dy * dy;
            if (lenSq > 0.01f)
            {
                float len = MathF.Sqrt(lenSq);
                FacingDirX = dx / len;
                FacingDirY = dy / len;
            }
        }

        // 맵 순환 — 경계를 넘으면 반대편으로
        X = ((x % MapW) + MapW) % MapW;
        Y = ((y % MapH) + MapH) % MapH;
    }

    public bool CanAttack()
        => (DateTime.UtcNow - _lastAttackAt).TotalSeconds >= AttackCooldownSec;

    public void ResetAttackCooldown()
        => _lastAttackAt = DateTime.UtcNow;

    public void IncreaseSpeed(float amount) => MoveSpeed = Math.Min(MoveSpeed + amount, 350f);
}
