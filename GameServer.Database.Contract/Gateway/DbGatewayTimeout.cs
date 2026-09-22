namespace GameServer.Database.Gateway;

/// <summary>
/// 동기 DB 호출의 제한 시간.
/// 로그인·가입·비밀번호 재설정은 3초, 관리 API 조회는 5초다.
/// 넘기면 <see cref="TimeoutException"/>을 던져 호출자가 실패를 구분한다.
/// 호출자가 이미 취소한 경우에는 취소가 그대로 전달된다.
/// </summary>
public static class DbGatewayTimeout
{
    public static readonly TimeSpan LoginAndRegister = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan AdminApi = TimeSpan.FromSeconds(5);

    public static async Task<T> Run<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await operation(cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DB 응답이 {timeout.TotalSeconds:0}초를 초과했습니다.");
        }
    }
}
