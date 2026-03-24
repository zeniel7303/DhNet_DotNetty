using DotNetty.Buffers;
using DotNetty.Codecs.Http.WebSockets;
using DotNetty.Transport.Channels;

namespace GameServer.Network;

/// <summary>
/// WebSocket 바이너리 프레임 ↔ IByteBuffer 변환 핸들러.
/// 인바운드: BinaryWebSocketFrame 수신 → 내용을 IByteBuffer로 추출하여 다음 핸들러(ProtobufDecoder)에 전달.
/// 아웃바운드: ProtobufEncoder가 출력한 IByteBuffer → BinaryWebSocketFrame으로 래핑하여 전송.
/// WebSocket 프레임이 이미 메시지 경계를 보장하므로 LengthField 프레이밍은 사용하지 않는다.
/// </summary>
sealed class WebSocketFrameHandler : ChannelDuplexHandler
{
    public override void ChannelRead(IChannelHandlerContext ctx, object msg)
    {
        switch (msg)
        {
            case BinaryWebSocketFrame frame:
                // 내용만 추출해서 다음 핸들러(ProtobufDecoder)로 전달
                ctx.FireChannelRead(frame.Content.Retain());
                frame.Release();
                break;

            case CloseWebSocketFrame closeFrame:
                closeFrame.Release();
                _ = ctx.CloseAsync();
                break;

            default:
                // PingWebSocketFrame 등 — 무시하고 해제
                if (msg is WebSocketFrame other) other.Release();
                break;
        }
    }

    public override Task WriteAsync(IChannelHandlerContext ctx, object msg)
    {
        // ProtobufEncoder 출력(IByteBuffer)을 BinaryWebSocketFrame으로 래핑
        if (msg is IByteBuffer buf)
            return ctx.WriteAsync(new BinaryWebSocketFrame(buf));

        return ctx.WriteAsync(msg);
    }
}
