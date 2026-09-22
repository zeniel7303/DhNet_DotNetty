using GameServer.Database.Gateway;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// 동기 DB 호출의 제한 시간. 초과는 TimeoutException, 호출자 취소는 취소 그대로다.
/// </summary>
public class DbGatewayTimeoutTests
{
    [Fact]
    public void Login_Is_Three_Seconds_And_Admin_Is_Five_Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), DbGatewayTimeout.LoginAndRegister);
        Assert.Equal(TimeSpan.FromSeconds(5), DbGatewayTimeout.AdminApi);
    }

    [Fact(Timeout = 5000)]
    public async Task Run_Returns_Value_When_Operation_Completes()
    {
        var value = await DbGatewayTimeout.Run(
            _ => Task.FromResult(7),
            DbGatewayTimeout.LoginAndRegister,
            CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact(Timeout = 5000)]
    public async Task Run_Throws_TimeoutException_When_Operation_Exceeds_Limit()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            DbGatewayTimeout.Run<int>(
                async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                },
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Contains("초과", ex.Message);
    }

    [Fact(Timeout = 5000)]
    public async Task Run_Propagates_Caller_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DbGatewayTimeout.Run(
                ct => Task.FromCanceled<int>(ct),
                DbGatewayTimeout.AdminApi,
                cts.Token));
    }
}
