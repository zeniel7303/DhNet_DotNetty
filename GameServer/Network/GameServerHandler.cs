using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Network;

public class GameServerHandler : SimpleChannelInboundHandler<GamePacket>
{
    private SessionComponent? _session;
    // CloseAsync() 이후 ChannelRead0 재진입 방지 — 로그/CloseAsync 중복 차단
    // I/O 이벤트 루프 단일 스레드에서만 접근하므로 일반 bool로 충분
    private bool _closing;

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
        if (_session == null || _closing) return;

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
                // 미인증 세션에서 게임 패킷 수신 시 즉시 연결 종료
                // 인증 후는 워커(틱 100ms)가 매 틱 큐 전체를 드레인하므로 큐 상한 불필요
                if (!_session.IsEntryHandshakeCompleted)
                {
                    if (!_closing)
                    {
                        _closing = true;
                        GameLogger.Warn("GameServerHandler",
                            $"미인증 세션 게임 패킷 수신 ({packet.PayloadCase}) — 연결 종료: {ctx.Channel.RemoteAddress}");
                        _ = ctx.CloseAsync();
                    }
                    return;
                }
                // 인증 후 패킷: 모든 정책(PacketPairPolicy, PacketRatePolicy) 검증 후 큐 적재
                // 정책 위반 시 연결 종료 — 로그는 SessionComponent.ProcessPacket에서 출력됨
                if (!_session.ProcessPacket(packet))
                {
                    if (!_closing)
                    {
                        _closing = true;
                        _ = ctx.CloseAsync();
                    }
                }
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
