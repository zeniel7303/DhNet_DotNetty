namespace EchoClient;

using System;
using System.Text;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Common;

public class EchoClientHandler : ChannelHandlerAdapter
{
    private readonly IByteBuffer _initialMessage;

    public EchoClientHandler()
    {
        _initialMessage = Unpooled.Buffer(ClientSettings.Size);
        var messageBytes = Encoding.UTF8.GetBytes("Hello world");
        _initialMessage.WriteBytes(messageBytes);
    }

    public override void ChannelActive(IChannelHandlerContext context) => context.WriteAndFlushAsync(this._initialMessage);

    public override void ChannelRead(IChannelHandlerContext context, object message)
    {
        if (message is IByteBuffer byteBuffer)
        {
            Console.WriteLine("Received from server: " + byteBuffer.ToString(Encoding.UTF8));
        }
        
        context.WriteAsync(message);
    }

    public override void ChannelReadComplete(IChannelHandlerContext context) => context.Flush();

    public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
    {
        Console.WriteLine("Exception: " + exception);
        context.CloseAsync();
    }
}