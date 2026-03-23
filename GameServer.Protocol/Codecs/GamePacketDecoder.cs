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
            output.Add(packet);
    }
}
