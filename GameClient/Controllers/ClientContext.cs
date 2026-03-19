using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Controllers;

public class ClientContext : IDisposable
{
    public int ClientIndex { get; init; }
    public ulong PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public bool RoomEnterSent { get; set; }
    public bool RoomExitScheduled { get; set; }
    public int RoomEnterRetryCount { get; set; }
    public int RoomLoopCount { get; set; }
    public int ReconnectCount { get; set; }
    public int TotalRoomCycles { get; set; }

    /// <summary>재접속 시 연결별 상태를 초기화합니다. 누적 카운터(ReconnectCount, TotalRoomCycles)는 유지됩니다.</summary>
    public void ResetForReconnect()
    {
        PlayerId = 0;
        PlayerName = string.Empty;
        RoomEnterSent = false;
        RoomExitScheduled = false;
        RoomEnterRetryCount = 0;
    }

    private Timer? _heartbeatTimer;

    public void StartHeartbeat(IChannel channel)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(_ =>
        {
            if (channel.Active)
                _ = channel.WriteAndFlushAsync(new GamePacket { ReqHeartbeat = new ReqHeartbeat() }).ContinueWith(
                    t => GameLogger.Error($"Client[{ClientIndex}]", "Heartbeat 전송 실패", t.Exception?.InnerException),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public void ScheduleRoomEnterRetry(IChannel channel)
    {
        RoomEnterRetryCount++;
        if (RoomEnterRetryCount <= 5)
        {
            var delay = 500 * RoomEnterRetryCount;
            var retry = RoomEnterRetryCount;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                GameLogger.Info($"Client[{ClientIndex}]", $"룸 입장 재시도 ({retry}/5)");
                await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                LoadTestStats.IncrementSent();
            }).ContinueWith(
                t => GameLogger.Error($"Client[{ClientIndex}]", "룸 입장 재시도 전송 실패", t.Exception?.InnerException),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        else
        {
            GameLogger.Warn($"Client[{ClientIndex}]", "룸 입장 재시도 한도 초과 (5회) - 포기");
        }
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }
}
