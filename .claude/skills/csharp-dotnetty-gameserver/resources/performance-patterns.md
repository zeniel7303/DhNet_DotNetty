# 성능 최적화 패턴
# Performance Patterns

## Channel<T> vs Lock 기반 JobQueue

이 프로젝트의 Lobby/Room은 `Channel<Action>`으로 락 없이 작업을 직렬화합니다.

```
Lock 기반:    acquire lock → 작업 → release lock   (경쟁 시 스레드 블로킹)
Channel 기반: TryWrite(job) → 즉시 반환 → 백그라운드 순차 처리  (논블로킹)
```

```csharp
// ✅ Channel<T>: 논블로킹 큐잉
_jobChannel.Writer.TryWrite(() => HandleEnter(player));

// ❌ lock: DotNetty EventLoop 스레드를 대기시킬 수 있음
lock (_lock) { HandleEnter(player); }
```

## ConcurrentDictionary 최적화

```csharp
// ✅ TryGetValue: 단일 원자적 조회
if (_players.TryGetValue(id, out var player))
    Process(player);

// ❌ ContainsKey + []: 두 번 접근, 경쟁 조건 가능
if (_players.ContainsKey(id))
    Process(_players[id]);  // 사이에 삭제될 수 있음!

// ✅ GetOrAdd: 없으면 생성, 있으면 반환 (원자적)
var room = _rooms.GetOrAdd(roomId, id => new Room(id));
```

## DotNetty EventLoop 부하 최소화

EventLoop 스레드에서 무거운 작업을 하면 **모든 채널에 영향**을 미칩니다.

```csharp
// ✅ ChannelRead0에서 즉시 Job Queue로 위임
protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
{
    if (_session == null) return;
    PacketRouter.Dispatch(_session, packet);  // 내부에서 EnqueueJob 호출
    // EventLoop 즉시 반환
}

// ❌ EventLoop에서 직접 무거운 처리
protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
{
    Thread.Sleep(100);       // EventLoop 블로킹! 모든 채널 영향
}
```

## 패킷 전송 최적화

```csharp
// ✅ WriteAndFlushAsync: 단일 원자적 작업 (일반적 사용)
await session.Channel.WriteAndFlushAsync(packet);

// ✅ Write + Flush 분리: 여러 패킷 배치 전송 시
channel.Write(packet1);
channel.Write(packet2);
await channel.FlushAsync();  // 한 번에 전송

// ❌ Write 후 Flush 없음: 패킷이 전송되지 않음
channel.Write(packet);  // 버퍼에만 쌓임
```

## Interlocked vs lock

```csharp
// ✅ 단순 카운터는 Interlocked (UniqueIdGenerator 패턴)
public ulong Generate() => (ulong)Interlocked.Increment(ref _counter);

// ❌ lock 오버헤드 불필요
lock (_lock) { _counter++; }
```
