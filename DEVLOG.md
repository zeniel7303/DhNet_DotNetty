# DEVLOG — DotNetty 멀티플레이어 게임 서버

C++ DhNet → C# DotNetty 재이식 과정의 개발 기록.
설계 결정, 구조 변경, 버그 수정의 맥락을 시간순으로 남긴다.

---

## 개발 로그

> 이 프로젝트는 [DotNetty](../DotNetty) 에서 시작됐다. 아래 이력은 그 과정을 포함한다.

---

### 2026-02-25 — 초기 구현 (DotNetty 프로젝트)

**C# .NET 9 + DotNetty로 멀티플레이어 게임 서버 첫 구현**

Protocol Buffers + `LengthFieldBasedFrameDecoder` 파이프라인, 로비/룸 게임 로직, 패킷 컨트롤러까지 Phase 1~5를 한 번에 구현했다.
이 시점 구조는 단일 핸들러에 세션 관리·패킷 라우팅·게임 로직이 뒤섞인 상태였다.

### 2026-02-26 — GameClient + 통합 테스트

테스트 클라이언트와 자동 시나리오(로그인 → 룸 입장 → 채팅 → 퇴장)를 구현했다.
Phase 7 통합 테스트에서 초기 버그를 다수 발견했다.

### 2026-03-03 — 부하 테스트 개편 + 초기 버그 수정

`LoadTest-Refactor`로 다중 클라이언트 구조, 로그/ID 분리, 부하 테스트 시나리오를 정비했다.
TOCTOU 정원 초과, 메모리 누수, 브로드캐스트 silent failure 등 High Priority 버그를 수정했다.
RoomEnter 경쟁 조건은 `_reservedCount` 예약 카운터 패턴으로 해결했다 — CAS 도입의 첫 시도.

### 2026-03-04 — DB 레이어 통합

`DatabaseSystem-Integration` / `DatabaseSystem-Improvements`로 MySQL + Dapper 기반 DB 레이어를 구축했다.
`GameLogContext`(채팅·입퇴장·통계 로그)를 별도 DB로 분리하고, `FireAndForget` 패턴의 기초를 잡았다.

### 2026-03-05 — 운영 인프라 완성

- **WebServer-Port**: ASP.NET Core REST API를 동일 프로세스에 통합 — SDK 충돌·네임스페이스 충돌 등 통합 이슈 해결
- **Config-Externalization**: `appsettings.json` 기반 설정 외부화 — 포트·DB 정보 하드코딩 제거
- **Heartbeat-Timeout**: `IdleStateHandler(30s)` 기반 유령 연결 감지
- **Critical-BugFix**: 코드 리뷰에서 발견한 경쟁 조건·보안·멱등성 버그 전수 수정

### 2026-03-06 ~ 03-17 — ECS 전면 리팩토링 (DotNetty 프로젝트)

단일 핸들러 구조의 한계가 명확해져 ECS 기반으로 전면 재설계했다.
`BaseComponent` / `BaseWorker` / `WorkerSystem`, `SessionSystem` 이벤트 큐, `RouterBuilder` 패킷 라우팅, `Common` 프로젝트 3분리를 단계적으로 구현했다.
이 리팩토링의 결론이 **DhNet_DotNetty 프로젝트**다 — 축적된 설계를 바탕으로 처음부터 다시 포팅했다.

---

### 2026-03-16 — 초기 포팅

**C++ DhNet → C# .NET 9 + DotNetty 기본 포팅 완성**

C++ 기반 DhNet 서버의 핵심 로직(로그인, 로비, 룸, 채팅)을 DotNetty 파이프라인 위에 옮겼다.
직렬화는 Protocol Buffers + `LengthFieldBasedFrameDecoder`로 구성했다.
이 시점에는 세션 관리, 패킷 라우팅, DB 레이어가 단일 핸들러에 뭉쳐 있었다.

---

### 2026-03-17 — 아키텍처 전면 재설계

**5개 대형 리팩토링이 하루에 집중됐다. 초기 구조의 문제가 한꺼번에 드러났기 때문이다.**

#### Common → Common.Shared / Common.Server / Common.Client 분리

단일 `Common` 프로젝트에 서버·클라이언트 코드가 섞여 불필요한 의존성이 생겼다.
기능별로 세 프로젝트로 분리해 서버 코드가 클라이언트 설정에 의존하지 않도록 했다.

#### switch-case → RouterBuilder 패턴

패킷 타입마다 `if/switch`로 분기하는 구조는 패킷 종류가 늘수록 핸들러가 비대해진다.
`RouterBuilder`로 타입 → 핸들러 맵을 빌드 시점에 등록하고, 수신 시 `Dictionary<Type, IRouter>`로 O(1) 디스패치한다.
중복 등록 시 즉시 예외를 발생시켜 실수를 조기에 잡는다.

#### 컴포넌트-워커 시스템 인프라 추가

패킷 도착마다 Task를 생성하는 방식은 GC 압력이 높고 처리 순서를 보장하기 어렵다.
`BaseComponent` / `BaseWorker<T>` / `WorkerSystem<T>` 구조를 도입해 고정 틱(100ms) 배치 처리로 전환했다.
`InstanceId % workerCount` 모듈로 분산으로 동일 플레이어가 항상 같은 워커에 배정된다 — lock 없이 패킷 처리가 직렬화된다.

#### GameSession → SessionComponent / SessionSystem 분리

세션 수명주기(연결·로그인·퇴장)를 I/O 스레드에서 직접 처리하면 순서 역전 문제가 생긴다.
`SessionSystem` 전용 스레드 + `ConcurrentQueue<EventData>`로 모든 수명주기 이벤트를 직렬 처리하도록 바꿨다.
`AttachPlayer → PlayerSystem.Add` 순서가 이벤트 큐로 보장되어 순서 역전 버그가 구조적으로 차단됐다.

#### Entity 제거, Component 기반 로비/룸/플레이어 재설계

`PlayerEntity` 같은 단일 거대 객체 대신 `PlayerComponent` + `PlayerLobbyComponent` + `PlayerRoomComponent`로 분리했다.
`LobbyComponent`와 `RoomComponent`는 CAS(`Interlocked.CompareExchange`)로 정원을 관리한다 — lock 없이 레이스 컨디션을 제거했다.

#### LoginProcessor 분리, 접속/로비 입장 플로우 개편

로그인 흐름이 `GameServerHandler` 안에 인라인으로 있어서 테스트와 수정이 어려웠다.
`LoginProcessor`를 별도 클래스로 추출하고, DB 조회 → `SessionSystem` 등록 → 로비 배정 순서를 명시적으로 관리하도록 했다.

#### ServerConstants 추출

`GameServerSettings`와 `PlayerSystem` 두 곳에 `MaxPlayers` 상수가 각각 존재했다.
`Common.Server/Constants.cs`로 통합해 단일 소스로 관리한다.

---

### 2026-03-18 — 버그 수정 및 기능 확장

#### ReqLobbyList 누락 수정

리팩토링 과정에서 `ReqLobbyList` 핸들러가 라우트 테이블에 등록되지 않아 패킷이 무시됐다.
`RouterBuilder`의 중복 체크 덕분에 빠르게 발견했다.

#### REST API 확장 + NotiSystem 패킷 추가

관리 목적의 API를 `/lobbies`, `/rooms`, `/players`, `/stats`, `/broadcast` 등으로 확장했다.
서버 → 클라이언트 시스템 공지용 `NotiSystem` 패킷을 proto에 추가하고 `broadcast` API와 연결했다.

#### Web API 인증 미들웨어 추가

인증 없이 열린 관리 API는 운영 환경에서 위험하다.
`ApiKeyMiddleware`(X-Api-Key 헤더)와 `IpWhitelistMiddleware`(IP 화이트리스트)를 추가했다.
`/health`는 인증 없이 접근 가능하도록 예외 처리했다.

#### 로비 수 10개로 확장, 초기화 기본값 제거

로비가 1개일 때 클러스터링 로직이 무의미했다. 10개로 늘려 `GetDefaultLobby()` 동작을 검증했다.

#### Web API Response 바디 로깅

운영 중 API 응답 내용을 확인하기 어려웠다.
`RequestLoggingMiddleware`에서 요청·응답 Body를 1KB 한도로 로깅하도록 추가했다.

#### 부하 테스트 Stats 개선, 채팅 로그 비활성화

부하 테스트 중 채팅 로그 DB 쓰기가 성능 측정을 왜곡했다. 기본값을 비활성화로 변경했다.
`LoadTestStats`에 `ChatSent` / `ChatRecv` / `RoomCycles` 카운터를 추가해 시나리오별 측정을 세분화했다.

---

### 2026-03-19 — 정리 및 배포 인프라

#### Legacy 프로젝트 제거

초기 실험용 EchoServer/Client, MemoryPack·MessagePack 직렬화 실험 코드를 제거했다.
직렬화 방식은 Protocol Buffers로 확정됐고, 실험 코드가 솔루션 빌드 시간을 늘리고 있었다.

#### Docker 지원 추가

로컬 환경 설정 없이 서버 + MySQL을 한 번에 띄울 수 있도록 `Dockerfile`과 `docker-compose.yml`을 추가했다.
`DOTNET_ENVIRONMENT=Docker` → `appsettings.Docker.json` 자동 로드로 컨테이너 환경을 분리했다.
`RequireConnection: true`를 Docker 전용으로 설정해 DB 없이 서버가 묵묵히 시작되는 문제를 방지했다.

---

## 기술적 도전 요약

### 🔄 동시성 — CAS / Lock-Free

| 문제 | 원인 | 해결 |
|------|------|------|
| 룸 정원 초과 (TOCTOU) | `_players.Count` 외부 읽기 → 낡은 값 참조 | `LobbyComponent._playerCount` CAS 예약 |
| Room 닫힘-예약 레이스 | `_reservedCount` + `_closing` 두 필드 분리 시 CAS 경쟁 | `_state` 단일 int 통합 (`-1`=닫히는 중) — 단일 CAS로 처리 |
| Leave → Enter 레이스 | RoomController에서 Leave/Enter를 연달아 호출 | Leave 이벤트 내부에서 Enter 호출 (동일 워커 스레드 직렬화) |

### ⏱️ 타이밍 & 순서

| 문제 | 원인 | 해결 |
|------|------|------|
| PlayerGameEnter 순서 역전 | `AttachPlayer`와 `PlayerSystem.Add` 사이 FIFO 미보장 | SessionSystem 이벤트 큐에 PlayerCreated → PlayerGameEnter 순서로 적재 |
| 로그인 중 연결 해제 | DB await 중 ChannelInactive 발화 → 정리 경로 누락 | `IsDisconnected` 체크 + `ImmediateFinalize()` / `DisconnectForNextTick()` 경로 분리 |
| PlayerSystem 좀비 잔류 | `Add` ↔ `session.Player` 사이 ChannelInactive 발화 → `DisconnectAsync` 미호출 | 순서 교체 + `channel.Active` 체크 + `Interlocked` 멱등성 가드 |
| DB 실패 시 유령 플레이어 | `players.Insert` 전에 로그인 응답 전송 | `await` 전환 + 실패 시 에러 응답 후 return |
| Room.Enter 실패 시 미아 상태 | catch 블록에서 Lobby 복구 누락 → 플레이어가 어디에도 속하지 않음 | catch에 `Lobby.TryEnter` 복귀 + 실패 응답 추가 |

### ⚡ 비동기 & 예외 처리

| 문제 | 원인 | 해결 |
|------|------|------|
| 비동기 예외 소실 | `_ = DbAsync()` 패턴이 예외를 삼킴 | `FireAndForget(tag)` 헬퍼로 예외 GameLogger 기록 |
| DotNetty void 경계 | `ChannelRead0/Inactive`가 `void` 반환 | 내부 async 체인 완성 후 경계에서만 `_ =` 처리 |
| `ContinueWith` 데드락 | `TaskScheduler.Current`가 I/O 스레드 스케줄러를 잡을 수 있음 | `TaskScheduler.Default` 명시 |
| ulong → long 오버플로우 | DB에서 읽은 `MAX(player_id)`를 long으로 캐스팅 | 상한 체크 + `ArgumentOutOfRangeException` |

### 🌐 네트워크 & 연결

| 문제 | 원인 | 해결 |
|------|------|------|
| 유령 연결 (Ghost Connection) | TCP keepalive가 수 시간 후에야 감지 | `IdleStateHandler(30s)` + 클라이언트 20초 Heartbeat 타이머 |
| Broadcast Silent Failure | Room 종료 직후 `Broadcast()`가 false 반환 → 200 OK | 반환값 검사 후 404 반환 |
| 비인터랙티브 환경 즉시 종료 | stdin EOF 시 `Console.ReadLine()` 즉시 반환 | `CancellationTokenSource` + Ctrl+C 시그널 |
| ASP.NET Core + GameServer 통합 | SDK 충돌, `RunAsync(ct)` API 부재, `LogLevel` 네임스페이스 충돌 | FrameworkReference, `ct.Register(Lifetime.StopApplication)`, 전체 한정자 |

### 🏗️ 아키텍처 & 설계

| 문제 | 원인 | 해결 |
|------|------|------|
| 다중 클라이언트 static 상태 | 전역 `static ClientState`가 클라이언트 간 공유 | 인스턴스 기반 `ClientContext`로 전환 |
| Common 프로젝트 결합도 | 서버/클라이언트 공용 단일 `Common` → 불필요한 의존성 | `Common.Shared` / `Common.Server` / `Common.Client` 3분리 |
| MaxPlayers 이중 관리 | `GameServerSettings`와 `PlayerSystem`에 각각 상수 존재 | `Common.Server/Constants.cs` 단일 소스로 통합 |
| 설정 하드코딩 | 포트/DB 등 변경마다 재빌드 필요 | `IConfiguration` — JSON / 환경변수 / CLI 오버라이드 계층 |
| Connection String Injection | DB 설정 직접 문자열 보간 — 특수문자 포함 시 파싱 깨짐 | `MySqlConnectionStringBuilder` 사용 |

### 🧪 부하 테스트

| 문제 | 원인 | 해결 |
|------|------|------|
| Thundering Herd | 1000명 동시 접속 시 연결 폭발 | `--delay N` CLI 옵션으로 접속 분산 + jitter 재접속 딜레이 |
| 재접속 루프 상태 오염 | 재접속 시 이전 세션 PlayerId/상태 잔류 | `ClientContext.ResetForReconnect()` 호출로 상태 초기화 |
