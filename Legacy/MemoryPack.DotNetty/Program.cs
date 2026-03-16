using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;

namespace MemoryPack.DotNetty
{
    public class MemoryPackEncoder : MessageToMessageEncoder<object>
    {
        protected override void Encode(IChannelHandlerContext context, object message, List<object> output)
        {
            var bytes = MemoryPackSerializer.Serialize(message);
            output.Add(Unpooled.WrappedBuffer(bytes));
        }
    }

    public class MemoryPackDecoder : MessageToMessageDecoder<IByteBuffer>
    {
        protected override void Decode(IChannelHandlerContext context, IByteBuffer message, List<object> output)
        {
            var bytes = new byte[message.ReadableBytes];
            message.GetBytes(message.ReaderIndex, bytes);
            var obj = MemoryPackSerializer.Deserialize<object>(bytes);
            output.Add(obj);
        }
    }
}