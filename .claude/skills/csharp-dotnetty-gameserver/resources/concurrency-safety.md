# 동시성 및 스레드 안전성
# Concurrency & Thread Safety

## ConcurrentDictionary 패턴

```csharp
// ✅ 올바른 방법
private readonly ConcurrentDictionary<IChannelId, GameSession> _sessions = new();

_sessions.TryAdd(session.Channel.Id, session);          // 추가
_sessions.TryRemove(session.Channel.Id, out _);         // 제거
_sessions.TryGetValue(channelId, out var session);      // 조회

// ✅ GetOrAdd: 없으면 생성, 있으면 기존 반환 (원자적)
var room = _rooms.GetOrAdd(roomId, id => new Room(id));

// ❌ ContainsKey + [] 조합: 경쟁 조건 발생 가능
if (_sessions.ContainsKey(id))
    Use(_sessions[id]);  // 사이에 삭제될 수 있음!

// ❌ 일반 Dictionary + lock: 성능 저하
private readonly Dictionary<IChannelId, GameSession> _sessions = new();
private readonly object _lock = new();
lock (_lock) { _sessions.Add(...); }
```

## Channel<T> 기반 Job Queue (Lobby/Room 패턴)

Lobby와 Room은 `Channel<Action>`으로 작업을 직렬화합니다. 락 없이 스레드 안전합니다.

```csharp
private readonly System.Threading.Channels.Channel<Action> _jobChannel =
    System.Threading.Channels.Channel.CreateUnbounded<Action>();

// 외부에서 안전하게 작업 위임 (논블로킹)
public void EnqueueJob(Action job) => _jobChannel.Writer.TryWrite(job);

// 백그라운드 처리 루프 (생성자에서 Task.Run으로 시작)
private async Task FlushAsync()
{
    await foreach (var job in _jobChannel.Reader.ReadAllAsync())
        job();  // 순차 처리 — 내부에서는 락 불필요
}
```

## DotNetty EventLoop 스레드 안전성

```csharp
// ✅ WriteAndFlushAsync: 스레드 안전
await session.Channel.WriteAndFlushAsync(packet);

// ✅ fire-and-forget 패턴 (응답 전송)
_ = session.SendAsync(packet);

// ❌ 여러 스레드에서 Write/Flush 분리 호출 금지
channel.Write(packet);
channel.Flush();
```

## 싱글톤 패턴

```csharp
// 프로세스 시작 시 한 번 생성, 이후 읽기 전용 → 락 불필요
public static readonly PlayerSystem Instance = new();
public static readonly GameSessionSystem Instance = new();
public static readonly LobbySystem Instance = new();
```

## Interlocked

```csharp
// ✅ 단순 카운터에 최적화 (UniqueIdGenerator 패턴)
private long _counter = 0;
public ulong Generate() => (ulong)Interlocked.Increment(ref _counter);
```
