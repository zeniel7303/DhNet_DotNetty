using Common.Server.Component;

namespace GameServer.Component.Player;

public class PlayerWorldComponent : BaseComponent
{
    public override void Initialize() { }
    protected override void OnDispose() { }
    public float X { get; private set; } = 100f;
    public float Y { get; private set; } = 100f;

    // 이동 속도 (px/s) — 클라이언트와 동일한 단위
    public float MoveSpeed { get; private set; } = 300f;

    /// <summary>캐릭터가 마지막으로 이동한 방향 (정규화). 기본값: 오른쪽 (1, 0).</summary>
    public float FacingDirX { get; private set; } = 1f;
    public float FacingDirY { get; private set; } = 0f;

    // 공격 쿨다운
    private DateTime _lastAttackAt = DateTime.MinValue;
    private static readonly TimeSpan AttackCooldown = TimeSpan.FromSeconds(0.3);

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
    }

    private const float MapW = 3200f;
    private const float MapH = 2400f;

    // dt 상한 — 클라이언트가 임의로 큰 dt를 보내 순간이동하는 것을 방지
    private const float MaxDtSec = 0.1f;

    /// <summary>
    /// 클라이언트 입력 플래그를 받아 서버에서 직접 이동을 적용한다.
    /// flags: bit0=W(위), bit1=S(아래), bit2=A(왼쪽), bit3=D(오른쪽)
    /// 클라이언트의 applyMovementInput()과 동일한 공식을 사용해야 한다.
    /// </summary>
    public void ApplyInput(uint flags, float dtSec)
    {
        dtSec = Math.Min(dtSec, MaxDtSec);

        float dirX = 0, dirY = 0;
        if ((flags & 1) != 0) dirY -= 1f; // W
        if ((flags & 2) != 0) dirY += 1f; // S
        if ((flags & 4) != 0) dirX -= 1f; // A
        if ((flags & 8) != 0) dirX += 1f; // D

        if (dirX != 0 || dirY != 0)
        {
            float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
            dirX /= len;
            dirY /= len;
            FacingDirX = dirX;
            FacingDirY = dirY;
        }

        float nx = X + dirX * MoveSpeed * dtSec;
        float ny = Y + dirY * MoveSpeed * dtSec;

        X = ((nx % MapW) + MapW) % MapW;
        Y = ((ny % MapH) + MapH) % MapH;
    }

    public bool CanAttack() => DateTime.UtcNow - _lastAttackAt >= AttackCooldown;

    public void ResetAttackCooldown() => _lastAttackAt = DateTime.UtcNow;

    public void IncreaseSpeed(float amount) => MoveSpeed = Math.Min(MoveSpeed + amount, 500f);
}
