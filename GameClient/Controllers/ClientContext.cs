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

    private Timer? _heartbeatTimer;

    public void StartHeartbeat(IChannel channel)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(_ =>
        {
            if (channel.Active)
                _ = channel.WriteAndFlushAsync(new GamePacket { ReqHeartbeat = new ReqHeartbeat() });
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
                _ = channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                LoadTestStats.IncrementSent();
            });
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
