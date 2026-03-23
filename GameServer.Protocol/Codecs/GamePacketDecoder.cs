using System.Diagnostics;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using GameServer.Protocol.Serialization;

namespace GameServer.Protocol.Codecs;

/// <summary>
/// IByteBuffer → GamePacket 변환. LengthFieldBasedFrameDecoder 뒤에 배치한다.
/// </summary>
public sealed class GamePacketDecoder : MessageToMessageDecoder<IByteBuffer>
{
    private readonly ISerializer _serializer;

    public GamePacketDecoder(ISerializer serializer) => _serializer = serializer;

    protected override void Decode(IChannelHandlerContext context, IByteBuffer message, List<object> output)
    {
        var bytes = new byte[message.ReadableBytes];
        message.ReadBytes(bytes);

        var packet = _serializer.Deserialize(bytes);
        if (packet != null)
        {
            output.Add(packet);
        }
        else
        {
            // malformed 패킷: PacketRatePolicy 카운트에 포함되지 않으므로 명시적으로 경고 출력
            Trace.TraceWarning(
                $"[GamePacketDecoder] 역직렬화 실패 — malformed 패킷 수신 from {context.Channel.RemoteAddress} ({bytes.Length} bytes)");
        }
    }
}
