namespace EchoServer;

using System;
using System.Text;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;

public class EchoServerHandler : ChannelHandlerAdapter
{
    public override void ChannelRead(IChannelHandlerContext context, object message)
    {
        if (message is IByteBuffer buffer)
        {
            Console.WriteLine("Received from client: " + buffer.ToString(Encoding.UTF8));
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