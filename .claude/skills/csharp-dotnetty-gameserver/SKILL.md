# C# DotNetty 게임 서버 개발 가이드라인
# C# DotNetty Game Server Development Guidelines

이 스킬은 DotNetty와 Protocol Buffers를 사용하는 C# 게임 서버 개발에 특화된 베스트 프랙티스와 패턴을 제공합니다.

This skill provides best practices and patterns specific to C# game server development using DotNetty and Protocol Buffers.

## 📚 리소스 (Resources)

이 스킬은 다음 리소스를 포함합니다:

### 핵심 아키텍처 (Core Architecture)
- **[architecture.md](resources/architecture.md)** - DotNetty 기반 게임 서버 아키텍처 패턴
  - 채널 파이프라인 설계
  - 핸들러 체인 구성
  - 부트스트랩 및 서버 초기화
  - 이벤트 루프 그룹 관리

### 네트워킹 (Networking)
- **[channel-handlers.md](resources/channel-handlers.md)** - DotNetty 채널 핸들러 패턴
  - 인바운드/아웃바운드 핸들러 구현
  - 핸들러 컨텍스트 사용법
  - 메시지 인코더/디코더 작성
  - 핸들러 순서 및 책임

- **[session-management.md](resources/session-management.md)** - 세션 및 연결 관리
  - 세션 생명주기 관리
  - 연결 상태 추적
  - 타임아웃 및 하트비트
  - 재연결 처리

### Protocol Buffers
- **[protobuf-patterns.md](resources/protobuf-patterns.md)** - Protocol Buffers 사용 패턴
  - 메시지 정의 베스트 프랙티스
  - 직렬화/역직렬화 최적화
  - 버전 관리 및 호환성
  - 메시지 ID 및 라우팅

### 게임 서버 시스템 (Game Server Systems)
- **[lobby-room-system.md](resources/lobby-room-system.md)** - 로비/룸 시스템 설계
  - 로비 매칭 로직
  - 룸 상태 관리
  - 플레이어 입장/퇴장 처리
  - 게임 시작/종료 플로우

- **[player-system.md](resources/player-system.md)** - 플레이어 시스템 관리
  - 플레이어 상태 추적
  - 인벤토리 및 데이터 관리
  - 플레이어 간 상호작용
  - 동기화 패턴

### 비동기 및 동시성 (Async & Concurrency)
- **[async-patterns.md](resources/async-patterns.md)** - C# 비동기 프로그래밍
  - async/await 베스트 프랙티스
  - Task 관리 및 취소
  - ConfigureAwait 사용법
  - 비동기 메서드 명명 규칙

- **[concurrency-safety.md](resources/concurrency-safety.md)** - 동시성 및 스레드 안전성
  - 스레드 안전 컬렉션
  - lock 및 동기화 패턴
  - ConcurrentDictionary 활용
  - 경쟁 조건 방지

### 에러 처리 및 로깅 (Error Handling & Logging)
- **[error-handling.md](resources/error-handling.md)** - 에러 핸들링 전략
  - 예외 처리 계층
  - 재시도 로직
  - 장애 격리
  - 우아한 종료

- **[logging-patterns.md](resources/logging-patterns.md)** - 로깅 베스트 프랙티스
  - 구조화된 로깅
  - 로그 레벨 전략
  - 성능에 미치는 영향 최소화
  - 분산 추적

### 성능 최적화 (Performance Optimization)
- **[memory-management.md](resources/memory-management.md)** - 메모리 관리 패턴
  - 객체 풀링
  - 버퍼 재사용
  - 가비지 컬렉션 최적화
  - 메모리 누수 방지

- **[performance-patterns.md](resources/performance-patterns.md)** - 성능 최적화 기법
  - 병목 지점 식별
  - 배치 처리
  - 캐싱 전략
  - 프로파일링 및 측정

## 🎯 적용 시점 (When This Skill Applies)

이 스킬은 다음 경우 자동으로 활성화됩니다:

### 파일 패턴 (File Patterns)
- `**/*.cs` - C# 소스 파일
- `**/*.proto` - Protocol Buffer 정의 파일
- `**/Handlers/**/*.cs` - DotNetty 핸들러 파일
- `**/Session/**/*.cs` - 세션 관리 파일

### 키워드 (Keywords)
- DotNetty, 채널, 핸들러, 파이프라인
- Protocol Buffers, protobuf, 직렬화
- 게임 서버, 로비, 룸, 플레이어
- async/await, Task, 비동기
- 세션, 연결, 네트워크
- 동시성, 스레드 안전성, lock

### 의도 패턴 (Intent Patterns)
- "DotNetty 채널 핸들러 구현"
- "Protocol Buffer 메시지 정의"
- "게임 서버 아키텍처 설계"
- "로비/룸 시스템 구현"
- "비동기 네트워크 처리"
- "멀티플레이어 동기화"

## 💡 핵심 원칙 (Core Principles)

### 1. 명확한 책임 분리 (Clear Separation of Concerns)
```csharp
// ✅ Good: 각 핸들러는 명확한 책임을 가집니다
public class ProtobufDecoder : MessageToMessageDecoder<IByteBuffer> { }
public class GamePacketHandler : SimpleChannelInboundHandler<GamePacket> { }
public class SessionManager : ChannelHandlerAdapter { }

// ❌ Bad: 하나의 핸들러가 너무 많은 책임을 가집니다
public class MegaHandler { /* 디코딩, 로직, 세션 관리 모두 처리 */ }
```

### 2. 비동기 우선 (Async First)
```csharp
// ✅ Good: 비동기 메서드 사용
public async Task<Player> GetPlayerAsync(int playerId)
{
    return await _database.Players.FindAsync(playerId);
}

// ❌ Bad: 동기 메서드로 블로킹
public Player GetPlayer(int playerId)
{
    return _database.Players.Find(playerId); // 스레드 블로킹!
}
```

### 3. 스레드 안전성 (Thread Safety)
```csharp
// ✅ Good: 스레드 안전 컬렉션 사용
private readonly ConcurrentDictionary<int, Session> _sessions
    = new ConcurrentDictionary<int, Session>();

// ❌ Bad: 일반 컬렉션 + lock (성능 저하)
private readonly Dictionary<int, Session> _sessions
    = new Dictionary<int, Session>();
private readonly object _lock = new object();
```

### 4. 리소스 관리 (Resource Management)
```csharp
// ✅ Good: IDisposable 패턴 사용
public class GameSession : IDisposable
{
    private IChannel _channel;

    public void Dispose()
    {
        _channel?.CloseAsync().Wait();
        _channel = null;
    }
}

// using 문으로 자동 해제
using (var session = new GameSession())
{
    // 사용
} // 자동으로 Dispose 호출
```

### 5. 에러 복원력 (Error Resilience)
```csharp
// ✅ Good: 예외를 적절히 처리하고 복구
public override void ExceptionCaught(IChannelHandlerContext ctx, Exception exception)
{
    _logger.LogError(exception, "Handler error for {Channel}", ctx.Channel.Id);

    // 복구 가능한 에러인지 판단
    if (IsRecoverable(exception))
    {
        // 재시도 또는 우회
    }
    else
    {
        ctx.CloseAsync();
    }
}
```

## 🚀 빠른 참조 (Quick Reference)

### DotNetty 핸들러 구현
```csharp
public class MyGameHandler : SimpleChannelInboundHandler<GamePacket>
{
    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket msg)
    {
        // 메시지 처리
    }

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        // 연결 시작
        base.ChannelActive(ctx);
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        // 연결 종료
        base.ChannelInactive(ctx);
    }
}
```

### Protocol Buffer 메시지 정의
```protobuf
syntax = "proto3";

message LoginRequest {
  string username = 1;
  string password = 2;
}

message LoginResponse {
  bool success = 1;
  string session_token = 2;
  int32 player_id = 3;
}
```

### 비동기 패킷 전송
```csharp
public async Task SendPacketAsync<T>(T packet) where T : IMessage
{
    var buffer = SerializePacket(packet);
    await _channel.WriteAndFlushAsync(buffer);
}
```

## 📖 더 알아보기 (Learn More)

각 리소스 파일에는 더 상세한 예제와 패턴이 포함되어 있습니다. 특정 주제에 대해 깊이 있는 정보가 필요하면 해당 리소스를 참조하세요.

---

**참고**: 이 가이드라인은 지속적으로 업데이트됩니다. 새로운 패턴이나 베스트 프랙티스가 발견되면 추가됩니다.
