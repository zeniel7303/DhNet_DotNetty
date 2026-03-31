using Common.Logging;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;

namespace GameServer.Network;

internal sealed class HeartbeatHandler : ChannelHandlerAdapter
{
    public static readonly HeartbeatHandler Instance = new();

    public override bool IsSharable => true;

    public override void UserEventTriggered(IChannelHandlerContext ctx, object evt)
    {
        if (evt is IdleStateEvent { State: IdleState.ReaderIdle })
        {
            GameLogger.Warn("Heartbeat", $"유휴 연결 감지, 강제 해제: {ctx.Channel.RemoteAddress}");
            _ = ctx.CloseAsync().ContinueWith(
                t => GameLogger.Error("Heartbeat", "CloseAsync 실패", t.Exception?.InnerException),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}
