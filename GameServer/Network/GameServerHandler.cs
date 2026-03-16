using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Controllers;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Network;

public class GameServerHandler : SimpleChannelInboundHandler<GamePacket>
{
    private GameSession? _session;

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        _session = new GameSession(ctx.Channel);
        GameSessionSystem.Instance.Register(_session);
        GameLogger.Info("연결", $"{ctx.Channel.RemoteAddress}");
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        _ = _session?.Player?.DisconnectAsync();
        if (_session != null)
        {
            GameSessionSystem.Instance.Unregister(_session);
        }
        GameLogger.Info("해제", $"{ctx.Channel.RemoteAddress}");
        _session = null;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
    {
        if (_session == null)
        {
            return;
        }
        PacketRouter.Dispatch(_session, packet);
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Error("예외", ex.Message, ex);
        ctx.CloseAsync();
    }
}
