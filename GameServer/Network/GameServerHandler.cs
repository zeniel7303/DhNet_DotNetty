using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Controllers;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Network;

public class GameServerHandler : SimpleChannelInboundHandler<GamePacket>
{
    private SessionComponent? _session;

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        _session = new SessionComponent(ctx.Channel);
        SessionSystem.Instance.EnqueueAdd(_session);
        GameLogger.Info("연결", $"{ctx.Channel.RemoteAddress}");
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        if (_session != null)
        {
            // SessionSystem이 Disconnect 처리 전담 — IsEntryHandshakeCompleted에 따라 경로 분기
            SessionSystem.Instance.EnqueueDisconnect(_session);
        }
        GameLogger.Info("해제", $"{ctx.Channel.RemoteAddress}");
        _session = null;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
    {
        if (_session == null) return;

        if (packet.PayloadCase == GamePacket.PayloadOneofCase.ReqLogin)
        {
            _ = LoginController.HandleAsync(_session, packet.ReqLogin);
        }
        else
        {
            var player = _session.Player;
            if (player != null)
            {
                player.Dispatch(packet);
            }
        }
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Error("예외", ex.Message, ex);
        _ = ctx.CloseAsync();
    }
}
