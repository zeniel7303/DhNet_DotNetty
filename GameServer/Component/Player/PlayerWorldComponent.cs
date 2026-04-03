using Common.Server.Component;

namespace GameServer.Component.Player;

public class PlayerWorldComponent : BaseComponent
{
    public override void Initialize() { }
    protected override void OnDispose() { }
    public float X { get; private set; } = 100f;
    public float Y { get; private set; } = 100f;

    // 기본 이동속도 200 (클라이언트 speed=5 × 60fps ≈ 300px/s 기준)
    public float MoveSpeed { get; private set; } = 200f;

    /// <summary>캐릭터가 마지막으로 이동한 방향 (정규화). 기본값: 오른쪽 (1, 0).</summary>
    public float FacingDirX { get; private set; } = 1f;
    public float FacingDirY { get; private set; } = 0f;

    // 공격 쿨다운 — dt 누산이 아닌 DateTime.UtcNow 기반으로 측정한다.
    // 이유: CanAttack()은 Stage 틱 스레드에서, ResetAttackCooldown()은 같은 틱 스레드에서 호출되지만
    //       dt 누산이면 PlayerComponent 워커 스레드(Update)에서도 _attackElapsed를 써야 한다.
    //       두 스레드가 float 필드를 공유하면 volatile + 비원자적 += dt 문제가 발생하므로,
    //       읽기 전용인 DateTime.UtcNow 방식으로 스레드 간 공유 상태 자체를 없앤다.
    private DateTime _lastAttackAt = DateTime.MinValue;
    private static readonly TimeSpan AttackCooldown = TimeSpan.FromSeconds(0.3);

    public void SetPosition(float x, float y)
    {
        X = x;
        Y = y;
    }

    private const float MapW = 3200f;
    private const float MapH = 2400f;

    // 이동 속도 검증 — 마지막 이동 시각 기준으로 최대 이동 거리 계산
    private DateTime _lastMoveAt = DateTime.MinValue;
    private const float MoveMargin  = 1.8f;  // 네트워크 지터 여유 계수
    private const float MaxMoveDtSec = 1.0f; // dt 상한 (서버 일시 지연 대비)

    /// <summary>
    /// 이동 속도를 검증한 뒤 위치를 갱신한다.
    /// 클라이언트가 보낸 좌표가 이동 속도 한계를 초과하면 false를 반환한다.
    /// 맵 순환 경계 점프(|dx| ≥ MapW*0.5)는 검증을 건너뛴다.
    /// </summary>
    public bool TryMove(float x, float y)
    {
        var now = DateTime.UtcNow;
        var dt = _lastMoveAt == DateTime.MinValue
            ? MaxMoveDtSec
            : (float)Math.Min((now - _lastMoveAt).TotalSeconds, MaxMoveDtSec);

        float dx = x - X;
        float dy = y - Y;

        // 맵 순환 경계 점프가 아닌 경우에만 속도 검증
        if (MathF.Abs(dx) < MapW * 0.5f && MathF.Abs(dy) < MapH * 0.5f)
        {
            float maxDist = MoveSpeed * dt * MoveMargin;
            if (dx * dx + dy * dy > maxDist * maxDist)
                return false;
        }

        _lastMoveAt = now;
        Move(x, y);
        return true;
    }

    private void Move(float x, float y)
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

    public bool CanAttack() => DateTime.UtcNow - _lastAttackAt >= AttackCooldown;

    public void ResetAttackCooldown() => _lastAttackAt = DateTime.UtcNow;

    public void IncreaseSpeed(float amount) => MoveSpeed = Math.Min(MoveSpeed + amount, 350f);
}
