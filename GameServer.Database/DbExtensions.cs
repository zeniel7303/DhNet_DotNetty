using Common.Logging;

namespace GameServer.Database;

/// <summary>
/// DB Task 관련 확장 메서드.
/// </summary>
public static class DbExtensions
{
    /// <summary>
    /// fire-and-forget DB 작업에 예외 로깅을 붙인다.
    /// await 없이 실행하되, 예외 발생 시 GameLogger.Error로 기록한다.
    /// </summary>
    public static void FireAndForget(this Task task, string tag)
    {
        task.ContinueWith(
            t =>
            {
                var ex = t.Exception!.Flatten();
                foreach (var inner in ex.InnerExceptions)
                    GameLogger.Error(tag, "DB 비동기 작업 실패", inner);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
