using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace MessagePack.DotNetty
{
    public class MessagePackEncoder : MessageToMessageEncoder<object>
    {
        protected override void Encode(IChannelHandlerContext context, object message, List<object> output)
        {
            var bytes = MessagePackSerializer.Serialize(message);
            output.Add(Unpooled.WrappedBuffer(bytes));
        }
    }

    public class MessagePackDecoder : MessageToMessageDecoder<IByteBuffer>
    {
        protected override void Decode(IChannelHandlerContext context, IByteBuffer message, List<object> output)
        {
            var bytes = new byte[message.ReadableBytes];
            message.GetBytes(message.ReaderIndex, bytes);
            var obj = MessagePackSerializer.Deserialize<object>(bytes);
            output.Add(obj);
        }
    }
}