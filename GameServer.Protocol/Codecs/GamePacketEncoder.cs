using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using GameServer.Protocol.Serialization;

namespace GameServer.Protocol.Codecs;

/// <summary>
/// GamePacket → IByteBuffer 변환. LengthFieldPrepender 앞에 배치한다.
/// </summary>
public sealed class GamePacketEncoder : MessageToByteEncoder<GamePacket>
{
    private readonly ISerializer _serializer;

    public GamePacketEncoder(ISerializer serializer)
    {
        _serializer = serializer;
    }

    protected override void Encode(IChannelHandlerContext context, GamePacket message, IByteBuffer output)
    {
        var bytes = _serializer.Serialize(message);
        output.WriteBytes(bytes);
    }
}
