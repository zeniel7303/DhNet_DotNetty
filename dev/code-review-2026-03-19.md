# 코드 리뷰 — 2026-03-19

> Opus 전체 코드 리뷰 결과 정리. 아키텍처, 버그, 보안, 성능 관점.

---

## 종합 평가

전체적으로 높은 수준. C++ 게임 서버 멀티스레딩 모델(전용 스레드 + 이벤트 큐)을 C#/.NET으로 충실하게 포팅하면서, DotNetty 비동기 파이프라인 / ConcurrentDictionary / Interlocked CAS 등 .NET best practice를 잘 적용함.

**특히 잘 된 부분:**
- `RoomComponent` — `_state` 단일 int로 예약 카운트 + 닫힘 상태를 CAS로 통합 관리. `TryReserve`와의 레이스 완전 제거
- `TaskScheduler.Default` 일관 명시 — DotNetty I/O 이벤트 루프 오염 방지
- 로그인 핸드셰이크 레이스 조건 — Disconnect 경쟁 경로가 주석으로 문서화
- Web API 3중 보안 — 요청 로깅 + IP 화이트리스트 + API 키
- 파이프라인 구성 — 프레이밍, 직렬화, 하트비트, 비즈니스 핸들러 단일 책임 분리

---

## 버그 / 즉시 수정 권장

### BUG-1: `_running` 필드 `volatile` 누락
**위치:** `Common.Server/Component/BaseWorker.cs:11`, `GameServer/Systems/SessionSystem.cs:43`

`_running = false`는 Stop 호출 스레드에서 설정되고, 워커 스레드(`Loop`)에서 읽힌다. `volatile`이 없으면 JIT 최적화에 의해 워커 스레드가 변경을 감지하지 못하고 무한 루프할 수 있다.

```csharp
// 현재
private bool _running;

// 수정
private volatile bool _running;
```

---

### BUG-2: `BaseComponent.Update()` 이벤트 드레인 — 개별 try/catch 없음
**위치:** `Common.Server/Component/BaseComponent.cs:57-60`

```csharp
// 현재 — 하나의 이벤트가 예외를 던지면 나머지 이벤트 전부 처리 안 됨
while (_eventQueue.TryDequeue(out var job))
{
    job();
}

// 수정
while (_eventQueue.TryDequeue(out var job))
{
    try { job(); }
    catch (Exception ex) { GameLogger.Error($"[Component] Event job failed: {ex}"); }
}
```

`Dispose()` 경로의 드레인에는 이미 try/catch가 있음 (79-88행). Update 경로에도 동일하게 적용 필요.

---

### BUG-3: `ImmediateFinalize()` — 미관측 Task
**위치:** `GameServer/Component/Player/PlayerComponent.cs:169`

```csharp
public void ImmediateFinalize() => _ = DisconnectAsync();
```

`DisconnectAsync` 내부에서 `PlayerSystem.Instance.Remove(this)` 등이 예외를 발생시키면 최상위 catch가 없어 unhandled Task가 된다.

```csharp
// 수정 — DisconnectAsync 최상위에 try/catch 추가
private async Task DisconnectAsync()
{
    try
    {
        // ... 기존 로직
    }
    catch (Exception ex)
    {
        GameLogger.Error($"[Player] DisconnectAsync failed: {ex}");
    }
}
```

---

## 안정성 / 유지보수성 개선

### IMP-1: `lock(this)` → 전용 lock 객체로 변경
**위치:** `GameServer/Component/Player/PlayerComponent.cs:127, 175`

`lock(this)`는 외부에서 동일 객체에 lock을 잡을 경우 데드락 위험이 있다. 현재 외부에서 lock하는 코드는 없으나 관례적으로 위험하다.

```csharp
// 수정
private readonly object _disconnectLock = new();

// lock(this) → lock(_disconnectLock)
```

---

### IMP-2: `GameLogger` — UnboundedChannel → BoundedChannel
**위치:** `Common.Shared/Logging/GameLogger.cs:9`

현재 `Channel.CreateUnbounded<LogEntry>()`를 사용 중. 로그 생산 속도가 소비 속도를 초과하면 메모리가 무한히 증가할 수 있다.

```csharp
// 수정
Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(4096)
{
    FullMode = BoundedChannelFullMode.DropOldest  // 또는 Wait
});
```

---

### IMP-3: `ResLogin` — 에러 코드 필드 없음
**위치:** `GameServer.Protocol/Protos/login.proto`

`player_id = 0`이 서버 정원 초과, DB 실패, 로비 만원 등 모든 실패를 나타낸다. 클라이언트가 실패 원인을 구분할 수 없다.

```protobuf
// 수정 제안
enum LoginResult {
  SUCCESS = 0;
  SERVER_FULL = 1;
  DB_ERROR = 2;
  LOBBY_FULL = 3;
}

message ResLogin {
  uint64 player_id = 1;
  LoginResult result = 2;
}
```

---

### IMP-4: `GameLogger` — 날짜 변경 시 로그 파일 롤링 없음
**위치:** `Common.Shared/Logging/GameLogger.cs`

서버 시작 시점의 날짜로 파일명이 고정되어, 장기 실행 시 하나의 파일에 계속 기록된다. 날짜가 바뀌면 새 파일로 롤링하는 기능이 필요하다.

---

## 보안

### SEC-1: API 키 비교 — 타이밍 공격 취약
**위치:** `GameServer/Web/Middleware/ApiKeyMiddleware.cs:19`

```csharp
// 현재 — 타이밍 공격 가능
if (provided != _apiKey)

// 수정
if (!CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(provided),
    Encoding.UTF8.GetBytes(_apiKey)))
```

---

### SEC-2: DB SSL 비활성화
**위치:** `GameServer/Systems/DatabaseSystem.cs:89`

`SslMode = MySqlSslMode.None` — 네트워크를 통한 DB 통신이 암호화되지 않는다. 프로덕션에서는 `SslMode = MySqlSslMode.Required` 이상으로 설정 필요.

---

### SEC-3: TCP 게임 서버 인증 없음
**위치:** `GameServer/Systems/LoginProcessor.cs`

`ReqLogin`에 `player_name`만 전송하면 로그인이 완료된다. 누구나 임의의 이름으로 접속 가능. `PlayerDbSet.cs:47` TODO 주석에서 인지하고 있음. 토큰 기반 인증 등 도입 필요.

---

## 성능

### PERF-1: `LobbySystem.TryGetLobby()` — 선형 탐색
**위치:** `GameServer/Systems/LobbySystem.cs:31`

`Array.Find`로 선형 탐색. 로비 수가 10개로 고정이므로 현재는 무시 가능하나, 확장 시 `Dictionary<ulong, LobbyComponent>`로 전환 권장.

---

### PERF-2: `_routeTable` → `FrozenDictionary`
**위치:** `GameServer/Component/Player/PlayerComponent.cs:23`

`_routeTable`은 `Initialize()` 시 한 번만 쓰고 이후 읽기 전용. .NET 8+ `FrozenDictionary`는 읽기 전용 딕셔너리에 최적화된 해시 테이블로 룩업 성능이 더 높다.

```csharp
using System.Collections.Frozen;

private FrozenDictionary<Type, IRouter> _routeTable = null!;

// Initialize에서
_routeTable = new Dictionary<Type, IRouter> { ... }.ToFrozenDictionary();
```

---

## 장기 개선 과제

### ARCH-1: 싱글톤 → DI 컨테이너 기반 전환
`SessionSystem.Instance`, `PlayerSystem.Instance`, `LobbySystem.Instance` 등 대부분의 시스템이 `static readonly` 싱글톤. Web API 레이어는 ASP.NET Core DI를 사용하면서도 내부적으로 싱글톤에 직접 접근해 일관성이 없다. 테스트 격리 불가, 단위 테스트 불가 상태.

---

## 수정 우선순위 요약

| 우선순위 | ID | 내용 | 파일 |
|----------|-----|------|------|
| 높음 | BUG-1 | `volatile _running` 누락 | `BaseWorker.cs:11`, `SessionSystem.cs:43` |
| 높음 | BUG-2 | Update 이벤트 개별 try/catch 없음 | `BaseComponent.cs:57` |
| 높음 | BUG-3 | `DisconnectAsync` 미관측 Task | `PlayerComponent.cs:169` |
| 중간 | IMP-1 | `lock(this)` → 전용 lock 객체 | `PlayerComponent.cs:127,175` |
| 중간 | IMP-2 | `GameLogger` BoundedChannel 적용 | `GameLogger.cs:9` |
| 중간 | IMP-3 | `ResLogin` 에러 코드 필드 추가 | `login.proto` |
| 중간 | SEC-1 | API 키 constant-time 비교 | `ApiKeyMiddleware.cs:19` |
| 낮음 | IMP-4 | 로그 파일 날짜 롤링 | `GameLogger.cs` |
| 낮음 | SEC-2 | DB SSL 활성화 | `DatabaseSystem.cs:89` |
| 낮음 | PERF-2 | `FrozenDictionary` 적용 | `PlayerComponent.cs:23` |
| 장기 | SEC-3 | TCP 인증 메커니즘 도입 | `LoginProcessor.cs` |
| 장기 | ARCH-1 | 싱글톤 → DI 전환 | 전체 Systems |
