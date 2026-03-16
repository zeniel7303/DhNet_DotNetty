# DotNetty 채널 핸들러 패턴
# DotNetty Channel Handler Patterns

## 핸들러 구현

```csharp
public class GamePacketHandler : SimpleChannelInboundHandler<GamePacket>
{
    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket msg)
    {
        // 메시지 처리
        ProcessPacket(ctx, msg);
    }

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        Console.WriteLine($"Client connected: {ctx.Channel.RemoteAddress}");
        base.ChannelActive(ctx);
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        Console.WriteLine($"Client disconnected: {ctx.Channel.RemoteAddress}");
        base.ChannelInactive(ctx);
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception exception)
    {
        Console.WriteLine($"Exception: {exception.Message}");
        ctx.CloseAsync();
    }
}
```

## 인코더/디코더

```csharp
public class ProtobufDecoder : MessageToMessageDecoder<IByteBuffer>
{
    protected override void Decode(IChannelHandlerContext context, IByteBuffer message, List<object> output)
    {
        var packet = DeserializePacket(message);
        output.Add(packet);
    }
}
```
