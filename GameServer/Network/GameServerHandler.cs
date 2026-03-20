using Common.Logging;
using DotNetty.Transport.Channels;
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

        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.ReqRegister:
                // TrySetRegisterStarted: 중복 ReqRegister 전송 시 RegisterProcessor 병렬 실행 방지
                if (_session.TrySetRegisterStarted())
                    _ = RegisterProcessor.ProcessAsync(_session, packet.ReqRegister);
                break;

            case GamePacket.PayloadOneofCase.ReqLogin:
                // TrySetLoginStarted: 중복 ReqLogin 전송 시 LoginProcessor 병렬 실행 방지
                if (_session.TrySetLoginStarted())
                    _ = LoginProcessor.ProcessAsync(_session, packet.ReqLogin);
                break;

            default:
                // 로그인 후 패킷은 세션 큐에 적재 — PlayerComponent 워커가 틱마다 드레인
                _session.EnqueuePacket(packet);
                break;
        }
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Error("예외", ex.Message, ex);
        _ = ctx.CloseAsync().ContinueWith(
            t => GameLogger.Error("GameServerHandler", "CloseAsync 실패", t.Exception?.InnerException),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
