using DotNetty.Codecs.Http;
using DotNetty.Codecs.Http.WebSockets;
using DotNetty.Codecs.Protobuf;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using GameServer.Protocol;

namespace GameServer.Network;

/// <summary>
/// WebSocket 연결용 파이프라인.
/// TCP 파이프라인(GamePipelineInitializer)과 달리 LengthField 프레이밍 없음
/// — WebSocket 프레임이 메시지 경계를 보장하기 때문.
/// 암호화는 브라우저 환경 특성상 생략 (TLS 레이어에서 보안 확보 가정).
/// </summary>
internal sealed class WsPipelineInitializer : ChannelInitializer<IChannel>
{
    private const string WsPath = "/ws";

    protected override void InitChannel(IChannel channel)
    {
        var pipeline = channel.Pipeline;

        // ── HTTP 업그레이드 (WebSocket 핸드셰이크) ─────────────────────
        pipeline.AddLast("http-codec",      new HttpServerCodec());
        pipeline.AddLast("http-aggregator", new HttpObjectAggregator(65_536));
        pipeline.AddLast("ws-protocol",     new WebSocketServerProtocolHandler(WsPath, subprotocols: null, allowExtensions: true));

        // ── WebSocket 프레임 ↔ ByteBuf 변환 ───────────────────────────
        pipeline.AddLast("ws-frame", new WebSocketFrameHandler());

        // ── 직렬화 ────────────────────────────────────────────────────
        pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
        pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());

        // ── 연결 관리 ─────────────────────────────────────────────────
        pipeline.AddLast("idle",      new IdleStateHandler(readerIdleTimeSeconds: 30, writerIdleTimeSeconds: 0, allIdleTimeSeconds: 0));
        pipeline.AddLast("heartbeat", HeartbeatHandler.Instance);
        pipeline.AddLast("handler",   new GameServerHandler());
    }
}
