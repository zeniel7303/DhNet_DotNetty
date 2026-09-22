using Common.Logging;
using GameServer.Database.Gateway;
using GameServer.Database.Rows;

namespace GameServer.Systems;

internal static class StatLogger
{
    private const int IntervalSeconds = 60;

    public static async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
                DbGateway.Current.WriteStatLog(new StatLogRow
                {
                    player_count = PlayerSystem.Instance.Count,
                    created_at   = DateTime.UtcNow
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
