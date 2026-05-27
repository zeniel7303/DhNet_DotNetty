using GameServer.Component.Player;
using GameServer.Resources;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// PlayerWorldComponent.ApplyInput — 이동 공식 검증.
/// 서버 단독 테스트. 클라이언트(game.js)와 공식이 동일해야 한다.
/// 기대값은 GameDataTable.Player / GameDataTable.Map에서 읽어 player.json 변경 시 자동으로 동기화된다.
/// </summary>
public class PlayerWorldTests : IClassFixture<GameDataFixture>
{
    public PlayerWorldTests(GameDataFixture fixture) { _ = fixture; }

    // ── 기본 이동 ─────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyInput_NoFlags_PositionUnchanged()
    {
        var w = new PlayerWorldComponent();
        (float x, float y) = (w.X, w.Y);

        w.ApplyInput(0u, 0.05f);

        Assert.Equal(x, w.X);
        Assert.Equal(y, w.Y);
    }

    [Theory]
    [InlineData(1u,  0f, -1f)]  // W → Y 감소
    [InlineData(2u,  0f,  1f)]  // S → Y 증가
    [InlineData(4u, -1f,  0f)]  // A → X 감소
    [InlineData(8u,  1f,  0f)]  // D → X 증가
    public void ApplyInput_SingleDirection_CorrectDelta(uint flags, float dirX, float dirY)
    {
        var w = new PlayerWorldComponent();
        w.SetPosition(1600f, 1200f); // 맵 중앙 (wrap 없음)
        float before_x = w.X, before_y = w.Y;
        const float dt = 0.1f;

        w.ApplyInput(flags, dt);

        float expectedDelta = GameDataTable.Player.InitialMoveSpeed * dt;
        Assert.Equal(before_x + dirX * expectedDelta, w.X, 3);
        Assert.Equal(before_y + dirY * expectedDelta, w.Y, 3);
    }

    // ── 대각선 정규화 ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyInput_Diagonal_SameDistanceAsStraight()
    {
        // 직선(W)
        var straight = new PlayerWorldComponent();
        straight.SetPosition(1600f, 1200f);
        straight.ApplyInput(1u, 0.1f);
        float straightDist = MathF.Abs(1200f - straight.Y);

        // 대각선(W+D) — 정규화 없으면 √2 배 빠름
        var diagonal = new PlayerWorldComponent();
        diagonal.SetPosition(1600f, 1200f);
        diagonal.ApplyInput(1u | 8u, 0.1f);
        float dx = diagonal.X - 1600f;
        float dy = diagonal.Y - 1200f;
        float diagDist = MathF.Sqrt(dx * dx + dy * dy);

        Assert.Equal(straightDist, diagDist, 3);
    }

    // ── dt 상한 ───────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyInput_LargeDt_ClampedToMaxDtSec()
    {
        float maxDt = GameDataTable.Map.MaxDtSec;

        var capped = new PlayerWorldComponent();
        capped.SetPosition(1600f, 1200f);
        capped.ApplyInput(1u, maxDt); // dt = MaxDtSec (최대)

        var huge = new PlayerWorldComponent();
        huge.SetPosition(1600f, 1200f);
        huge.ApplyInput(1u, maxDt * 99f); // 매우 큰 dt → MaxDtSec로 클램프

        Assert.Equal(capped.Y, huge.Y, 3);
    }

    // ── 맵 경계 wrap-around ───────────────────────────────────────────────────

    [Fact]
    public void ApplyInput_WrapAroundLeft_XBecomesNearMapEnd()
    {
        float delta = GameDataTable.Player.InitialMoveSpeed * 0.1f;
        float mapW  = GameDataTable.Map.MapWidth;
        float startX = delta * 0.5f; // 이동 후 음수가 되어 wrap 발생

        var w = new PlayerWorldComponent();
        w.SetPosition(startX, 1200f);
        w.ApplyInput(4u, 0.1f); // A

        Assert.True(w.X > mapW * 0.9f, $"Expected wrap to near {mapW}, got {w.X}");
    }

    [Fact]
    public void ApplyInput_WrapAroundTop_YBecomesNearMapEnd()
    {
        float delta = GameDataTable.Player.InitialMoveSpeed * 0.1f;
        float mapH  = GameDataTable.Map.MapHeight;
        float startY = delta * 0.5f;

        var w = new PlayerWorldComponent();
        w.SetPosition(1600f, startY);
        w.ApplyInput(1u, 0.1f); // W

        Assert.True(w.Y > mapH * 0.9f, $"Expected wrap to near {mapH}, got {w.Y}");
    }

    // ── 속도 증가 및 상한 ─────────────────────────────────────────────────────

    [Fact]
    public void IncreaseSpeed_AddsAmount()
    {
        var w = new PlayerWorldComponent();
        float amount = 25f;
        float expected = GameDataTable.Player.InitialMoveSpeed + amount;

        w.IncreaseSpeed(amount);

        Assert.Equal(expected, w.MoveSpeed, 1);
    }

    [Fact]
    public void IncreaseSpeed_CapsAtMaxMoveSpeed()
    {
        var w = new PlayerWorldComponent();
        w.IncreaseSpeed(9999f);

        Assert.Equal(GameDataTable.Player.MaxMoveSpeed, w.MoveSpeed);
    }

    // ── 이동 방향 추적 ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyInput_UpdatesFacingDir_WhenMoving()
    {
        var w = new PlayerWorldComponent();
        w.ApplyInput(8u, 0.1f); // D → 오른쪽

        Assert.Equal(1f,  w.FacingDirX, 3);
        Assert.Equal(0f,  w.FacingDirY, 3);
    }

    [Fact]
    public void ApplyInput_DoesNotChangeFacingDir_WhenStationary()
    {
        var w = new PlayerWorldComponent();
        float prevX = w.FacingDirX, prevY = w.FacingDirY;
        w.ApplyInput(0u, 0.1f); // 입력 없음

        Assert.Equal(prevX, w.FacingDirX);
        Assert.Equal(prevY, w.FacingDirY);
    }
}
