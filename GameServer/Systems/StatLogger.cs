using Common.Logging;
using GameServer.Database;
using GameServer.Database.Rows;

namespace GameServer.Systems;

static class StatLogger
{
    private const int IntervalSeconds = 60;

    public static async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
                DatabaseSystem.Instance.GameLog.StatLogs.InsertAsync(new StatLogRow
                {
                    player_count = PlayerSystem.Instance.Count,
                    created_at   = DateTime.UtcNow
                }).FireAndForget("StatLogger");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
