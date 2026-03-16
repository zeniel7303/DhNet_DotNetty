# 메모리 관리 패턴
# Memory Management Patterns

## IByteBuffer 자동 해제

`SimpleChannelInboundHandler<T>` 사용 시 `ChannelRead0` 완료 후 **자동으로 ReferenceCount 해제**됩니다.

```csharp
// ✅ SimpleChannelInboundHandler: 자동 해제 (이 프로젝트 표준)
public class GameServerHandler : SimpleChannelInboundHandler<GamePacket>
{
    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
    {
        // packet 사용 후 자동 release — 수동 처리 불필요
        PacketRouter.Dispatch(_session, packet);
    }
}

// ❌ ChannelHandlerAdapter 직접 사용 시 수동 해제 필수
public override void ChannelRead(IChannelHandlerContext ctx, object msg)
{
    try { /* 처리 */ }
    finally { ReferenceCountUtil.Release(msg); }  // 누락 시 메모리 누수
}
```

## 파이프라인 버퍼 흐름

```
[소켓]
  → LengthFieldBasedFrameDecoder  (내부 버퍼 관리)
  → ProtobufDecoder               (IByteBuffer → GamePacket 변환, 버퍼 자동 해제)
  → GameServerHandler             (GamePacket 사용, SimpleChannelInboundHandler가 해제)
```

## Channel<T> 정리

Lobby/Room 종료 시 Channel Writer를 반드시 완료합니다.

```csharp
public void Shutdown()
{
    _jobChannel.Writer.Complete();  // ReadAllAsync 루프 종료
}
```

## EventLoopGroup 정상 종료

```csharp
finally
{
    await Task.WhenAll(
        bossGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
        workerGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
}
```

## 베스트 프랙티스

### ✅ Do
- 모든 핸들러에 `SimpleChannelInboundHandler<T>` 사용
- `Channel<T>.Writer.Complete()` 로 채널 정상 종료
- `EventLoopGroup.ShutdownGracefullyAsync()` 로 정상 종료

### ❌ Don't
- `IByteBuffer.Retain()` 후 `Release()` 누락
- `ChannelHandlerAdapter` 에서 `ReferenceCountUtil.Release` 생략
- `Channel<T>` Writer 완료 없이 프로세스 종료
