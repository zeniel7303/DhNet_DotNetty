using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Scenarios;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Network;

public class GameClientHandler : SimpleChannelInboundHandler<GamePacket>
{
    private readonly ClientContext _ctx;
    private readonly ILoadTestScenario _scenario;

    public GameClientHandler(ClientContext ctx, ILoadTestScenario scenario)
    {
        _ctx = ctx;
        _scenario = scenario;
    }

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        _ctx.StartHeartbeat(ctx.Channel);
        _ = _scenario.OnConnectedAsync(ctx.Channel, _ctx);
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
    {
        if (packet.PayloadCase == GamePacket.PayloadOneofCase.ResHeartbeat)
            return;
        _ = _scenario.OnPacketReceivedAsync(ctx.Channel, _ctx, packet);
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        _ctx.Dispose();
        _scenario.OnDisconnected(_ctx);
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Error($"Client[{_ctx.ClientIndex}]", ex.Message, ex);
        LoadTestStats.IncrementErrors();
        ctx.CloseAsync();
    }
}
