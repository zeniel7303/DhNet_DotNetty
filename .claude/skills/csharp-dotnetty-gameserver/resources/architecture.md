# DotNetty 게임 서버 아키텍처
# DotNetty Game Server Architecture

## 개요 (Overview)

DotNetty 기반 게임 서버는 고성능 비동기 네트워크 통신을 위한 이벤트 기반 아키텍처를 사용합니다.

## 핵심 컴포넌트 (Core Components)

### 1. 부트스트랩 (Bootstrap)
서버 초기화 및 설정을 담당합니다.

```csharp
var bossGroup = new MultithreadEventLoopGroup(1);
var workerGroup = new MultithreadEventLoopGroup();

var bootstrap = new ServerBootstrap()
    .Group(bossGroup, workerGroup)
    .Channel<TcpServerSocketChannel>()
    .Option(ChannelOption.SoBacklog, 100)
    .ChildHandler(new ActionChannelInitializer<ISocketChannel>(channel =>
    {
        IChannelPipeline pipeline = channel.Pipeline;
        pipeline.AddLast(new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
        pipeline.AddLast(new ProtobufDecoder());
        pipeline.AddLast(new GamePacketHandler());
        pipeline.AddLast(new ProtobufEncoder());
    }));

await bootstrap.BindAsync(port);
```

### 2. 채널 파이프라인 (Channel Pipeline)
메시지 처리 체인을 구성합니다.

```
[소켓] → [FrameDecoder] → [ProtobufDecoder] → [GameHandler] → [ProtobufEncoder] → [소켓]
```

### 3. 이벤트 루프 그룹 (Event Loop Group)
- **Boss Group**: 새 연결 수락
- **Worker Group**: 연결된 채널의 I/O 처리

## 베스트 프랙티스 (Best Practices)

### ✅ Do
- 각 핸들러는 단일 책임을 가져야 합니다
- 비동기 작업에는 async/await 사용
- 리소스는 반드시 해제 (IDisposable)
- 스레드 안전 컬렉션 사용

### ❌ Don't
- 핸들러에서 블로킹 작업 수행
- 이벤트 루프에서 무거운 작업 실행
- 예외를 무시
- 채널을 수동으로 관리
