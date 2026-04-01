# DEVLOG — DotNetty 멀티플레이어 게임 서버

C++ DhNet → C# DotNetty 재이식 과정의 개발 기록.
설계 결정, 구조 변경, 버그 수정의 맥락을 시간순으로 남긴다.

---

## 개발 로그

> 이 프로젝트는 DotNetty 프로젝트에서 시작됐다. 아래 이력은 그 과정을 포함한다.

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

#### Graceful Shutdown 구현

서버를 강제 종료하면 처리 중이던 세션과 DB 쓰기가 유실될 수 있었다.
`ShutdownSystem` 싱글톤이 `CancellationTokenSource`를 소유하고, `Interlocked.Exchange`로 단일 진입을 보장한다.
`POST /shutdown` REST API로 원격에서도 종료를 트리거할 수 있다(202 반환 후 200ms 지연 → CTS 발행).
`GameSystems.StopAsync()`가 `SessionSystem` 정리 → `PlayerSystem.WaitUntilEmptyAsync(30s)` 순으로 실행해 DB 동기화 완료 후 프로세스가 종료된다.
모든 시스템 생명주기(시작/종료)를 `GameSystems`로 단일화해 `ServerStartup` 로직을 단순화했다.

---

### 2026-03-20 — AES-128-GCM 패킷 암호화 (Phase 1)

**모든 게임 패킷을 AES-128-GCM으로 암호화하는 레이어를 파이프라인에 추가했다.**

#### 구현 결정

패킷 스니핑 방어가 목표였다. 대칭키 방식 중 AES-128-GCM을 선택한 이유:
- **기밀성 + 무결성을 단일 패스로 처리** — 변조된 패킷은 auth-tag 불일치로 즉시 거부된다
- **AES-128 vs AES-256** — 게임 서버 수준에서 128-bit 키는 충분하고, 256-bit 대비 연산 비용이 낮다
- **Nonce 랜덤 생성** — 패킷마다 `RandomNumberGenerator`로 12바이트 생성, Nonce 재사용 없음

와이어 포맷: `[2B length] [12B nonce] [N bytes ciphertext] [16B auth-tag]`

#### 파이프라인 삽입 위치

```
framing-dec → [crypto-dec] → [crypto-enc] → protobuf-decoder
```

framing이 패킷 경계를 먼저 분리한 뒤 암호화 레이어가 처리한다.
비활성화 시(`Encryption.Key = ""`): 핸들러 자체를 파이프라인에 추가하지 않는다.

#### 추가 요청 반영

- **Fail-fast 키 검증**: 처음엔 각 `ConnectClientAsync`에서 Base64 파싱을 개별 호출했다. 파싱 실패가 "연결 실패"로 오보고되는 문제를 코드 리뷰에서 발견, `RunAsync` 시점에 한 번만 파싱하도록 수정했다.
- **서버/클라 기본값 동기화**: `appsettings.json`과 `LoadTestConfig.EncryptionKey`를 동일한 키(`AiZROpbIadx1uVbp64v7nQ==`)로 설정 — 인자 없이 실행해도 암호화가 자동으로 활성화된다.
- **SHA-256 방식 시도 → 롤백**: "CHANGE-THIS-ENCRYPTION-KEY" 같은 가독성 있는 문자열을 지원하기 위해 SHA-256 도출 방식을 시도했으나, 원래 설계인 Base64 방식이 더 명확하다고 판단해 롤백했다.

#### 부하 테스트 결과 (로컬, 1000 클라이언트, lobby-chat, 30초)

| 구간 | 암호화 ON | 암호화 OFF |
|------|-----------|-----------|
| 최대 Active | 1000 | 1000 |
| 총 ChatRecv (25s) | 7,853,911 | 8,587,297 |
| 처리량 | 약 377,000 패킷/s | 약 429,000 패킷/s |
| 오버헤드 | **~12%** | — |
| 에러 | 0 | 0 |

게임 서버 수준에서 허용 가능한 범위. AES-GCM 암호화/복호화 + Nonce 랜덤 생성 비용이다.

---

### 2026-03-20 — 계정 인증 시스템 (Phase 2)

**회원가입/로그인 인증 레이어를 서버에 추가했다.**

#### 설계 결정

초기 설계는 BotToken(서버에 사전 공유된 비밀 키)을 `ReqLogin`에 포함해 서버가 자동으로 계정을 생성하는 방식이었다.
이 방식은 일반 사용자와 봇의 인증 경로가 이원화돼 서버 로직이 복잡해지는 문제가 있어 폐기했다.

**최종 결정: `ReqRegister` → `ReqLogin` 단일 플로우**

```
클라이언트 접속 시 항상 ReqRegister 먼저 전송
  → SUCCESS(신규 가입) 또는 USERNAME_TAKEN(기존 계정)
  → 둘 다 ReqLogin 진행
```

봇과 일반 사용자가 동일한 경로를 사용한다. `USERNAME_TAKEN`을 정상 흐름으로 처리하면 재접속 봇은 별도 로직 없이 기존 계정으로 로그인된다.

#### 핵심 구현

**INSERT IGNORE + rows_affected 이중 체크**

`UNIQUE KEY`가 있는 테이블에 동시 요청이 들어오면 SELECT → INSERT 사이에 TOCTOU가 발생한다.
`INSERT IGNORE`를 쓰면 DB 레벨에서 중복을 무시하고 rows_affected가 0을 반환한다 — 예외 없이 중복을 안전하게 처리한다.

**이름 신뢰성: DB username 우선**

```csharp
var player = new PlayerComponent(session, account.username); // req.Username 무시
```

클라이언트가 보낸 이름 대신 DB의 `accounts.username`을 사용한다. 클라이언트 측 이름 조작이 원천 차단된다.

**User Enumeration 방지**

username이 없어도, password가 틀려도 동일한 `INVALID_CREDENTIALS`를 반환한다.
공격자가 응답 코드로 유효한 username을 식별할 수 없다.

**길이 검증 — 서버 양쪽에서**

`RegisterProcessor`뿐 아니라 `LoginProcessor`에도 4~16자 검증을 추가했다.
코드 리뷰에서 발견: DB 조회 전에 차단하지 않으면 형식이 맞지 않는 입력이 매번 쿼리를 유발한다.

#### 보안 고려사항 (Phase 3 대기 중)

현재는 비밀번호를 평문으로 DB에 저장한다(`password_hash` 컬럼명은 Phase 3을 위한 예약).
네트워크 전송 구간은 AES-GCM으로 보호되지만 DB 유출 시 비밀번호가 노출된다.

Phase 3에서 BCrypt 해싱으로 교체할 때 주의할 점:
- 전환 포인트는 `RegisterProcessor.cs` 1곳, `LoginProcessor.cs` 1곳으로 최소화됐다
- username 미존재 시에도 dummy hash로 `BCrypt.Verify`를 실행해야 Timing Attack을 방어할 수 있다 — 응답 시간 차이로 username 존재 여부가 노출되기 때문이다

#### SRP 검토 및 기각

"서버도 비밀번호를 몰라야 하지 않냐"는 질문에서 SRP(Secure Remote Password) 도입을 검토했다.
SRP는 수학적 증명 교환으로 서버가 평문을 전혀 보지 않는다.

기각 이유:
- 2-RTT(패킷 4번) — 현재 단일 `ReqLogin` 구조를 전면 변경해야 한다
- C#용 안정적인 SRP 라이브러리가 없다
- 전송 구간은 AES-GCM으로 이미 보호된다

결론: **BCrypt + AES-GCM**이 게임 서버 수준에서 충분하다.

---

### 2026-03-20 — 셧다운 안정화 및 BaseComponent disposed 가드

셧다운 흐름과 BaseComponent 이벤트 큐에서 두 가지 문제를 수정했다.

#### BaseComponent.EnqueueEvent — disposed 가드 + TCS hang 방지

`Dispose()` drain 완료 후 다른 스레드가 `EnqueueEvent()`를 호출하면 job이 큐에 추가되지만 처리되지 않는다.
특히 `EnqueueEventAsync`가 TCS를 생성한 뒤 job이 실행되지 않으면 `await`가 영원히 반환되지 않는다.

해결: `EnqueueEvent()`를 `bool` 반환으로 변경했다. `false`는 disposed로 인해 enqueue가 일어나지 않았음을 의미한다.
`EnqueueEventAsync` 오버로드가 반환값을 보고 `false`면 즉시 `TrySetCanceled()`로 TCS를 취소한다.

```csharp
var enqueued = EnqueueEvent(() => { ...; tcs.TrySetResult(); });
if (!enqueued) tcs.TrySetCanceled();
```

이 패턴은 "워커 스레드에서 완료를 await하되, 컴포넌트가 종료됐으면 취소로 빠져나온다"는 의도를 코드로 명확히 표현한다.

#### SessionSystem.Stop() — Join 타임아웃 5s → 30s + 타임아웃 감지 로그

`DisconnectAll()` 후 5초 내에 모든 세션이 정리되지 않으면 Join이 early return되어 Disconnect가 미완료인 채로 `PlayerSystem.WaitUntilEmptyAsync`가 진행된다.
`Thread.Join(TimeSpan)`의 반환값(`bool`)을 활용해 타임아웃 소진 여부를 감지하고 Error 로그를 남긴다 — 근본 원인 추적을 위해.

---

### 2026-03-23 — account_id 스키마 정제, 패킷 정책 시스템, 중복 로그인 방지

#### account_id 스키마 정제 — INSERT 후 SELECT 제거

회원가입 시 `INSERT → SELECT(account_id 조회)` 2-step 패턴을 사용하고 있었다.
DB 왕복이 한 번 더 발생하고, INSERT 직후 SELECT가 실패하는 예외 경로도 존재했다.

`IdGenerators.Account.Next()`로 account_id를 서버에서 미리 생성해 INSERT에 포함하는 방식으로 변경했다.
SELECT가 불필요해졌고, `player_id` 생성기를 `account_id` 생성기로 이름을 정리해 ID 의미가 명확해졌다.

#### 패킷 정책 시스템 도입 — 미인증 세션 보호 + 어뷰징 차단

로그인 완료 전 게임 패킷이 수신되거나, 동일 타입 패킷이 과도하게 쌓이는 경우에 대한 방어가 없었다.

`IPacketPolicy` 인터페이스를 도입하고 세 가지 정책을 구현했다:
- **PacketHandshakePolicy**: 미인증 세션에서 게임 패킷 수신 시 즉시 연결 종료 (auth gate)
- **PacketPairPolicy**: 큐에 동일 타입 패킷이 이미 적재된 경우 중복 차단 (delegate 주입으로 큐 접근)
- **PacketRatePolicy**: 슬라이딩 윈도우 기반 초당 패킷 수 제한 (기본 60pps)

`DrainPackets` 소유권을 `PlayerComponent`에서 `SessionComponent`로 이관했다.
`SessionComponent.PacketHandler` 델리게이트로 라우팅을 분리해 정책 적용 후 처리 흐름이 단일화됐다.

#### 동일 계정 다중 로그인 방지 — TryReserveLogin 패턴 (BUG-2)

두 세션이 동시에 같은 계정으로 로그인을 시도하면, 두 요청 모두 `_players`에 없는 것을 확인하고 진행해 중복 등록이 가능했다(TOCTOU).

`PlayerSystem._reservedAccounts`를 도입했다:
- DB Insert **전**에 account_id를 `ConcurrentDictionary`에 원자적으로 예약 (`TryAdd`)
- 이미 예약됐거나 활성인 경우 `ALREADY_LOGGED_IN(1006)` 응답 후 즉시 반환
- `Add()`에서 `_players` 추가 완료 후 reservation 제거 (역순 시 TOCTOU 발생 가능)
- `Remove()`에서 예약 잔류 안전망 정리, DB Insert 실패 시 `ImmediateFinalize`로 누수 방지

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
| `EnqueueEventAsync` 무한 대기 | `Dispose()` drain 완료 후 다른 스레드가 job을 enqueue하면 처리되지 않아 TCS가 영원히 미완료 | `EnqueueEvent()`를 `bool` 반환으로 변경 — `false`이면 즉시 `TrySetCanceled()` |

### 🌐 네트워크 & 연결

| 문제 | 원인 | 해결 |
|------|------|------|
| 유령 연결 (Ghost Connection) | TCP keepalive가 수 시간 후에야 감지 | `IdleStateHandler(30s)` + 클라이언트 20초 Heartbeat 타이머 |
| Broadcast Silent Failure | Room 종료 직후 `Broadcast()`가 false 반환 → 200 OK | 반환값 검사 후 404 반환 |
| 비인터랙티브 환경 즉시 종료 | stdin EOF 시 `Console.ReadLine()` 즉시 반환 | `CancellationTokenSource` + Ctrl+C 시그널 |
| ASP.NET Core + GameServer 통합 | SDK 충돌, `RunAsync(ct)` API 부재, `LogLevel` 네임스페이스 충돌 | FrameworkReference, `ct.Register(Lifetime.StopApplication)`, 전체 한정자 |
| SessionSystem 종료 타임아웃 미감지 | `DisconnectAll()` 후 5초 내 드레인 실패 시 Join이 조용히 반환 | `Thread.Join(30s)` 반환값(`bool`) 검사 + 타임아웃 소진 시 Error 로그 |

### 🏗️ 아키텍처 & 설계

| 문제 | 원인 | 해결 |
|------|------|------|
| 다중 클라이언트 static 상태 | 전역 `static ClientState`가 클라이언트 간 공유 | 인스턴스 기반 `ClientContext`로 전환 |
| Common 프로젝트 결합도 | 서버/클라이언트 공용 단일 `Common` → 불필요한 의존성 | `Common.Shared` / `Common.Server` / `Common.Client` 3분리 |
| MaxPlayers 이중 관리 | `GameServerSettings`와 `PlayerSystem`에 각각 상수 존재 | `Common.Server/Constants.cs` 단일 소스로 통합 |
| 설정 하드코딩 | 포트/DB 등 변경마다 재빌드 필요 | `IConfiguration` — JSON / 환경변수 / CLI 오버라이드 계층 |
| Connection String Injection | DB 설정 직접 문자열 보간 — 특수문자 포함 시 파싱 깨짐 | `MySqlConnectionStringBuilder` 사용 |

### 2026-03-24 — HTML5 WebSocket 게임 클라이언트 + RPG 기반 구축

**브라우저 기반 인게임 클라이언트를 추가하고 서버를 Vampire Survivors 스타일 RPG로 전환했다.**

#### GameClient.Web 추가

`protobufjs@7` CDN + WebSocket으로 서버에 바로 접속하는 HTML5 클라이언트를 구축했다.
Canvas 2D로 맵/플레이어/몬스터를 렌더링하고, 로비 → 룸 → 인게임 화면 전환을 단일 HTML로 처리한다.

#### 서버 RPG 기반 구성

- **포트 분리**: TCP 7777(기존 로비/룸) 유지, WebSocket 7778 추가 (브라우저 전용)
- **새 컴포넌트**: `PlayerCharacterComponent`(레벨/HP/ATK), `PlayerWorldComponent`(위치/이동), `MonsterComponent`(AI), `StageComponent`(웨이브/전투)
- **게임 루프**: `StageComponent`가 100ms 틱에서 몬스터 AI, 웨이브 스포너, 무기 시스템을 순차 실행

#### 패킷 큐 완화

게임 입장 시 HP·위치 초기화 누락으로 클라이언트가 0좌표에서 시작하는 버그를 수정했다.
이동/전투 패킷이 큐 제한에 걸려 드랍되는 문제는 `PacketPairPolicy` 허용치를 완화해 해결했다.

---

### 2026-03-25 — 뱀파이어 서바이벌 RPG 전면 개선

**게임 플레이의 핵심 시스템(무기, 웨이브, AI, 렌더링)을 대대적으로 개선했다.**

#### 서버 핵심 개선

- **wrap-aware AI**: 맵 순환(3200×2400)에서 몬스터가 최단 경로로 플레이어를 추적하도록 dx/dy modulo 보정
- **핫루프 최적화**: `MathF.Sqrt` 대신 거리 제곱 비교(`DistSq`), `volatile _hp`로 안전한 읽기
- **웨이브 스포너 버그 수정**: `_elapsed += dt` 누적 오류 → `_elapsed -= WaveInterval`로 정확한 틱 처리
- **무기 사거리 상한**: `MaxRangeSq = 400²` 추가, 투사체가 맵 밖으로 무한 진행하던 문제 차단
- **구조 분리**: GemManager, WaveSpawner, WeaponSystem, Weapons를 StageComponent에서 분리

#### 클라이언트 핵심 개선

- **클라이언트 사이드 예측**: 로컬 키 입력을 즉시 렌더링, 50ms 스로틀로 서버 전송 빈도 제어
- **Canvas 2D 프로시저럴 스프라이트**: 저작권 없는 직접 그린 스프라이트 9종(슬라임·오크·드래곤·박쥐·좀비·스켈레톤·유령·거대좀비·리퍼)
- **lerpWrap()**: 맵 경계에서 몬스터가 순간이동하지 않도록 최단 방향 보간
- **toScreen() wrap-aware**: 카메라 오프셋에 맵 순환 적용

#### 골드 시스템 추가

몬스터 처치 시 골드 드랍, 레벨업 선택지에 골드 관련 업그레이드 추가.
인게임 스탯(HP·ATK·속도 등)을 세션별로 초기화하도록 정책 적용 — 전 게임 스탯 누적 버그 차단.

#### 아키텍처 리팩토링 (Opus 리뷰 반영)

`Component/Stage` 디렉토리 재구조화, 코드 컨벤션 정리, `BroadcastPacket` lock 이동으로 잠재적 데드락 제거.

#### BCrypt 인증 전환 (Phase 3)

`accounts.password_hash`에 BCrypt 해싱 적용 완료. Phase 2 "평문 저장" 상태가 해소됐다.
`LoginProcessor`와 `RegisterProcessor` 두 곳만 수정됐다 — Phase 2 당시 교체 포인트를 최소화해둔 설계 덕분.
username 미존재 시에도 dummy hash로 `BCrypt.Verify`를 실행해 Timing Attack 방어 유지.

---

### 2026-03-26 — 무기 다양화: 단검·도끼 + 레벨업 시스템

**투사체 무기 2종을 추가하고 레벨업 선택지 큐 시스템을 구현했다.**

#### 단검 (KnifeWeapon)

`PlayerWorldComponent.FacingDirX/Y`(이동 방향) 기준으로 직선 투사체를 발사한다.
`Move()` 호출 시 facing 방향이 갱신되며, 맵 순환 점프는 방향 갱신에서 제외한다.
기존 마늘(GarlicWeapon) 대신 **기본 시작 무기**로 변경 — 첫 플레이 경험 개선.

#### 도끼 (AxeWeapon)

중력 포물선(`y(t) = y0 + velY*t + 0.5*1000*t²`) 투사체. 관통, 최대 5발 동시 유지.
클라이언트에서 해석적 공식으로 동일하게 계산 → 서버 동기화 없이 부드러운 궤적 표현.

#### 레벨업 선택지 큐

레벨업이 연속으로 발생할 때 선택창이 유실되던 문제를 큐로 해결했다.
HP·ATK·DEF·이동속도·경험치배율 5종 중 랜덤 3개를 제시한다.

#### 클라이언트 버그 수정

- 이동 중단 시 캐릭터 위치 싱크 깨짐: 마지막 이동 패킷 전송 후 서버 보정값 수신 타이밍 문제
- `NotiCombat.weaponId` 필드 누락: proto-bundle.json 재생성으로 해결

---

### 2026-03-30 — 무기 3종 추가 + 히트박스 밸런싱

**마법 지팡이·성경·십자가 무기를 추가하고 충돌 판정 정확도를 높였다.**

#### 마법 지팡이 (WandWeapon)

가장 가까운 적을 자동 조준하는 직선 투사체. 비관통. 기본 무기가 단검에서 **마법 지팡이**로 교체됐다.

#### 성경 (BibleWeapon)

플레이어 주위를 공전하는 오비탈 무기. 매 틱 `NotiOrbitalWeaponSync`로 각도를 동기화한다.
다중 무기 인스턴스를 지원하도록 `orbitalWeaponList` 배열로 클라이언트 관리.

#### 십자가 (CrossWeapon)

sin 궤적 왕복 부메랑. `Lifetime(1.4s)` 절반 지점에서 귀환 페이즈로 전환, 전진·귀환 각 1회 관통.
클라이언트에서 `sin(π*t/1.4)` 공식으로 동일 궤적 재현.

#### 히트박스 밸런싱

몬스터 종류마다 `HitRadius`를 개별 지정 (Bat=8f, Reaper=22f 등).
단검 히트반경을 확대해 체감 명중률 개선.

---

### 2026-03-31 — 디렉토리 구조 정리 + 플레이어 비주얼

서버 디렉토리 구조와 네이밍 컨벤션을 정리했다. `Component/Stage/` 하위 폴더 정렬, 파일명 일관성 확보.
브라우저 클라이언트의 플레이어 캐릭터 이미지를 교체했다.

---

### 2026-04-01 — 서버 아키텍처 리팩토링 + 게임플레이 개선

**4개 리팩토링으로 서버 구조를 정비하고, 도끼 비주얼·젬 흡수·50웨이브 스케일링을 구현했다.**

#### RoomSystem 도입 + BaseComponent 상속 통일

`StageComponent`의 `RunTickAsync/_cts/_tickTask` 자체 루프를 제거하고, `RoomSystem`(WorkerSystem)이 `RoomComponent.Update → StageComponent.Update`를 구동하도록 통일했다.
Player/Stage/Monster 서브컴포넌트 전체가 `BaseComponent`를 상속하면서 생명주기가 일원화됐다.
`LobbyComponent`가 `RoomComponent` 생명주기를 소유하는 책임 구조가 명확해졌다.

#### PlayerSaveComponent 도입

`PlayerComponent`에 혼재된 DB 저장 로직을 `PlayerSaveComponent`로 분리했다.
`MarkDirty` 기반 지연 저장 패턴으로 불필요한 DB 쓰기를 줄였다.
`AddGold`에서 `MarkDirty` 호출 누락 버그도 함께 수정됐다.

#### StageComponent 전투/패킷 로직 분리

`StageComponent`에 집중된 전투·브로드캐스트 로직을 `StageCombatHelper`와 `StageBroadcastHelper`로 추출했다.
`StageComponent`는 틱 진입점·입력 큐·게임 종료 조건만 관리한다.

#### Gem 객체 풀링 + GemComponent 인라인화

`GemComponent`의 얇은 래퍼가 불필요한 GC 압박을 유발하고 있었다.
`Gem` 내부 클래스로 인라인화하고 `Stack<Gem>` 풀로 재사용 — 젬 스폰/수집이 많은 후반 웨이브에서 GC 감소.

#### 도끼 비주얼 개선

🪓 이모지를 20px → **40px**로 2배 확대. 오른쪽 진행 시 `ctx.scale(-1, 1)`으로 방향 반전 적용.

#### 경험치 젬 흡수 애니메이션

`NotiGemCollect` 수신 시 젬을 즉시 삭제하는 대신, 클라이언트에서 600ms 동안 플레이어 위치로 ease-out 이동 애니메이션을 재생한다. `lerpWrap()` 기반으로 맵 순환 경계를 처리한다. 서버 추가 프로토콜 없이 클라이언트 독립 실행.

#### 50웨이브 스케일링

- **Reaper 최종 보스**: wave 50에만 등장 (기존 10의 배수마다 등장 → 클리어가 너무 빨랐음)
- **몬스터 스탯 스케일링**: 웨이브마다 HP/ATK 8% 증가 (Wave 50 ≈ 4.9배)
- **최대 몬스터**: 200 → **500**
- **Ghost**: wave 6부터 등장 추가, Bat/Zombie 상한 증가

### 🧪 부하 테스트

| 문제 | 원인 | 해결 |
|------|------|------|
| Thundering Herd | 1000명 동시 접속 시 연결 폭발 | `--delay N` CLI 옵션으로 접속 분산 + jitter 재접속 딜레이 |
| 재접속 루프 상태 오염 | 재접속 시 이전 세션 PlayerId/상태 잔류 | `ClientContext.ResetForReconnect()` 호출로 상태 초기화 |

### 🔒 보안

| 문제 | 원인 | 해결 |
|------|------|------|
| 패킷 스니핑 | 평문 프로토버프 패킷이 TCP에 노출 | AES-128-GCM 암호화 레이어 (framing 직후 삽입) |
| 변조 패킷 감지 | 암호화만으로 무결성 보장 불가 | GCM auth-tag 불일치 → 즉시 연결 종료 |
| Nonce 재사용 | 정적 Nonce 사용 시 평문 복원 가능 | 패킷마다 `RandomNumberGenerator`로 12바이트 랜덤 생성 |
| 잘못된 키 오보고 | Base64 파싱 실패가 개별 연결 실패로 기록됨 | 서버 시작 시 Fail-fast 검증 (한 번만 파싱) |
| DB 비밀번호 평문 저장 | 인증 미구현 상태 | accounts 테이블 + 회원가입/로그인 프로세서 구현 (Phase 3에서 BCrypt로 교체 예정) |
| User Enumeration | username 존재 여부를 에러 코드로 노출 | username 없음·password 불일치 모두 `INVALID_CREDENTIALS` 동일 응답 |
| 이름 조작 | 클라이언트가 임의 이름으로 로그인 가능 | 로그인 성공 시 DB `accounts.username` 값만 사용 (req 입력 무시) |
| Log Injection | 검증 실패 로그에 외부 입력 원문 포함 | 길이 정보만 기록, 원문 username 제거 |
| 미인증 세션 게임 패킷 수신 | 로그인 완료 전 게임 패킷 수신 가능 | `PacketHandshakePolicy` — auth gate, 즉시 연결 종료 |
| 패킷 어뷰징 (flooding/중복) | 동일 타입 패킷 과다 전송으로 큐 적재 | `PacketPairPolicy`(동일 타입 중복 차단), `PacketRatePolicy`(60pps 슬라이딩 윈도우) |
| 동일 계정 다중 로그인 (TOCTOU) | DB Insert 전 `_players` 확인만으로는 동시 요청 차단 불가 | `TryReserveLogin` — `_reservedAccounts` CAS 예약으로 레이스 윈도우 원천 차단 |
