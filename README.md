# DotNetty 멀티플레이어 게임 서버

C++ 기반 게임 서버(DhNet)를 C# .NET 9 + DotNetty로 재이식한 프로젝트.
단순 이식에서 시작해 비동기 아키텍처 설계, 동시성 버그 수정, DB 레이어 통합, 부하 테스트 인프라, 관리용 REST API 웹 서버까지 단계적으로 발전시켰다.

**기술 스택**: C# .NET 9, DotNetty 0.7.6, Protocol Buffers, MySQL(MySqlConnector + Dapper), ASP.NET Core

---

## 목차

1. [전체 아키텍처](#전체-아키텍처)
2. [핵심 설계 원칙](#핵심-설계-원칙)
3. [프로젝트 구조](#프로젝트-구조)
4. [기술적 도전 요약](#기술적-도전-요약)
5. [실행 방법](#실행-방법)
6. [Docker 실행](#docker-실행)

---

## 전체 아키텍처

```
[GameClient (n개 병렬)] ──TCP:7777──▶ [DotNetty Pipeline]
                                           LengthFieldBasedFrameDecoder
                                           LengthFieldPrepender(2)
                                           ProtobufDecoder(GamePacket.Parser)
                                           ProtobufEncoder
                                           IdleStateHandler(30s)
                                           HeartbeatHandler
                                           GameServerHandler
                                                │
                              ┌─────────────────┴──────────────────┐
                         ReqLogin 수신                        기타 패킷 수신
                              │                                     │
                         LoginProcessor                  session.EnqueuePacket()
                         (TaskPool 비동기)                           │
                              │                            [WorkerSystem, 100ms 틱]
                         SessionSystem                    PlayerComponent.Update()
                         (전용 스레드,                     DrainSessionPackets()
                          이벤트 큐 기반)                            │
                              │                            _routeTable[type].Handle()
                         PlayerSystem                               │
                         WorkerSystem 등록               ┌──────────┴───────────┐
                                              PlayerLobbyController  PlayerRoomController
                                              PlayerHeartbeatController
                                                         │
                                              LobbyComponent / RoomComponent
                                              (CAS 기반 동시성 제어)
                                                         │
                        ┌────────────────────────────────────────────────────┐
                        │            GameServer.Database (싱글톤)             │
                        │  GameDbContext  → gameserver DB → PlayerDbSet       │
                        │  GameLogContext → gamelog DB                        │
                        │    ├── LoginLogDbSet                                │
                        │    ├── ChatLogDbSet                                 │
                        │    ├── RoomLogDbSet                                 │
                        │    └── StatLogDbSet                                 │
                        └────────────────────────────────────────────────────┘

[Browser / 관리 도구] ──HTTP:8080──▶ [ASP.NET Core Web Server]
                                           (ApiKeyMiddleware + IpWhitelistMiddleware)
                                           GET  /health
                                           GET  /lobbies, POST /lobbies/{id}/broadcast
                                           GET  /rooms, GET /rooms/{id}
                                           POST /rooms/{id}/broadcast
                                           GET  /players, POST /players/{id}/kick
                                           GET  /stats, GET /stats/history
                                           GET  /server-info
                                           POST /broadcast
                                           GET  /analytics/chat-logs|login-logs|room-logs
                                                │ In-Process
                                     PlayerSystem / LobbySystem / DatabaseSystem
```

---

## 핵심 설계 원칙

### 1. SessionSystem 전용 스레드 — 세션 수명 주기 직렬화

`SessionSystem`은 전용 스레드 + `ConcurrentQueue<EventData>`로 세션의 모든 생명주기 이벤트를 순차 처리한다.
DotNetty I/O EventLoop → LoginProcessor(TaskPool) → SessionSystem 순으로 책임이 분리되어, `AttachPlayer`와 `PlayerSystem.Add` 간 순서 역전 문제를 원천 차단한다.

```
[I/O EventLoop]       ChannelRead0(ReqLogin) → _ = LoginProcessor.ProcessAsync(...)
[LoginProcessor]      DB await → EnqueuePlayerCreated → SessionSystem: AttachPlayer
(TaskPool)            → EnqueuePlayerGameEnter → SessionSystem: PlayerSystem.Add → TCS.SetResult
[WorkerSystem]        (TCS 완료 이후) 패킷 처리 시작
```

### 2. WorkerSystem — 플레이어별 워커 고정으로 직렬성 보장

`PlayerComponent`는 `WorkerSystem<PlayerComponent>`에 등록된다.
`InstanceId % workerCount`로 동일 플레이어가 항상 동일 워커에 고정되어, 패킷 처리(`DrainSessionPackets`)가 단일 스레드에서 직렬 실행된다.

```csharp
// WorkerSystem: 모듈로 분산
int index = (int)((item.InstanceId & long.MaxValue) % workerCount);
_workers[index].Add(item);  // item.Initialize() 후 등록

// PlayerComponent 워커 틱 (100ms)
public override void Update(float dt)
{
    if (IsDisposed) return;
    DrainSessionPackets();  // 세션 큐 전체 드레인
}
```

### 3. RouterBuilder — 타입 기반 O(1) 패킷 디스패치

`PlayerComponent` 초기화 시 세 컨트롤러가 라우트 테이블에 등록된다.
수신 패킷의 타입을 `ExtractPayload()`로 추출해 `Dictionary<Type, IRouter>`에서 O(1) 조회 후 핸들러 호출.

```csharp
// 등록 (Initialize 시 1회)
NewRouter()
    .With<ReqLobbyList>(OnLobbyList)   // PlayerLobbyController
    .With<ReqLobbyChat>(OnChat)
    .With<ReqRoomEnter>(OnRoomEnter)
    .With<ReqRoomChat>(OnChat)         // PlayerRoomController
    .With<ReqRoomExit>(OnExit)
    .With<ReqHeartbeat>(_ => ResHeartbeat)  // PlayerHeartbeatController
    .Build();

// 처리 (워커 틱마다)
var (type, payload) = packet.ExtractPayload();
if (_routeTable.TryGetValue(type, out var router))
    router.Handle(payload, response => _ = Session.SendAsync(response));
```

### 4. CAS 기반 무락 동시성 — LobbyComponent / RoomComponent

여러 PlayerComponent 워커가 동시에 입장/퇴장을 시도할 때, 락 없이 `Interlocked.CompareExchange`로 처리.

**LobbyComponent — 정원 예약:**
```csharp
do {
    current = _playerCount;
    if (current >= MaxCapacity) return false;
} while (Interlocked.CompareExchange(ref _playerCount, current + 1, current) != current);
```

**RoomComponent — `_state` 단일 int 통합 (`-1`=닫히는 중, `0~N`=예약 수):**
```csharp
// 마지막 슬롯 반환 시 _state를 -1로 전환 — TryReserve와의 레이스 원천 차단
int next = current == 1 ? -1 : current - 1;
if (Interlocked.CompareExchange(ref _state, next, current) == current)
    return next == -1;  // 마지막 반환자가 방 삭제 책임
```

### 5. LobbySystem 멀티 로비 + 클러스터링

`LobbySystem`은 N개의 `LobbyComponent`를 관리한다.
`GetDefaultLobby()`는 가득 차지 않은 로비 중 **현재 인원이 가장 많은** 로비를 반환해, 소수 접속 시 여러 로비에 분산되지 않고 한 로비에 클러스터링한다.

### 6. GamePacket oneof 래퍼 — 17종 패킷 단일 타입 처리

```protobuf
message GamePacket {
  oneof payload {
    ReqLogin      req_login       = 1;
    ResLogin      res_login       = 2;
    ReqRoomEnter  req_room_enter  = 3;
    ResRoomEnter  res_room_enter  = 4;
    NotiRoomEnter noti_room_enter = 5;
    ReqRoomChat   req_room_chat   = 6;
    NotiRoomChat  noti_room_chat  = 7;
    ReqRoomExit   req_room_exit   = 8;
    ResRoomExit   res_room_exit   = 9;
    NotiRoomExit  noti_room_exit  = 10;
    ReqLobbyChat  req_lobby_chat  = 11;
    NotiLobbyChat noti_lobby_chat = 12;
    ReqHeartbeat  req_heartbeat   = 13;
    ResHeartbeat  res_heartbeat   = 14;
    ReqLobbyList  req_lobby_list  = 15;
    ResLobbyList  res_lobby_list  = 16;
    NotiSystem    noti_system     = 17;  // 서버→클라이언트 시스템 공지
  }
}
```

---

## 프로젝트 구조

```
DhNet_DotNetty/
├── Common.Shared/
│   ├── Helper.cs                    DotNetty 콘솔 로거 설정, 실행 경로 유틸
│   └── Logging/                     GameLogger (채널 기반 비동기 로거), LogEntry, LogLevel
│
├── Common.Server/
│   ├── Constants.cs                 ServerConstants.MaxPlayers = 1000 (기본값)
│   ├── GameServerSettings.cs        GamePort(7777), WebPort(8080), MaxPlayers
│   ├── DatabaseSettings.cs          Host/Port/UserId/Password/Database/LogDatabase/RequireConnection
│   ├── ServerSettings.cs            통합 설정 루트
│   ├── Component/
│   │   ├── BaseComponent.cs         이벤트 큐(ConcurrentQueue) + Initialize/Update/Dispose 기반 클래스
│   │   ├── BaseWorker.cs            워커 스레드 (100ms 틱, 컴포넌트 Add/Remove/Update)
│   │   └── WorkerSystem.cs          N개 워커 풀, InstanceId % workerCount 모듈로 분산
│   └── Routing/
│       ├── IRouter.cs               Handle(request, callback) + GetRequestType()
│       ├── PacketRouter.cs          응답 없는/있는 제네릭 라우터 구현체
│       └── RouterBuilder.cs         빌더 패턴 라우트 등록, 중복 체크
│
├── Common.Client/
│   └── ClientSettings.cs            클라이언트 설정 (현재 미사용)
│
├── GameServer.Protocol/
│   └── Protos/                      .proto 6개 → C# 자동 생성 (Grpc.Tools)
│       ├── game_packet.proto        GamePacket oneof 래퍼 (17종)
│       ├── login.proto              ReqLogin, ResLogin
│       ├── lobby.proto              ReqLobbyChat, NotiLobbyChat, ReqLobbyList, LobbyInfo, ResLobbyList
│       ├── room.proto               ReqRoomEnter/Res/Noti, ReqRoomChat/Noti, ReqRoomExit/Res/Noti
│       ├── heartbeat.proto          ReqHeartbeat, ResHeartbeat
│       └── system.proto             NotiSystem { message } — 서버→클라이언트 시스템 공지
│
├── GameServer.Database/
│   ├── DatabaseSystem.cs            싱글톤, GameDbContext + GameLogContext 초기화, MaxId 조회
│   ├── DbExtensions.cs              Task.FireAndForget(tag) — 예외 시 GameLogger.Error
│   ├── System/
│   │   ├── DbConnector.cs           MySqlConnector + Dapper 래퍼 (Query/Execute/Transaction)
│   │   ├── GameDbContext.cs         gameserver DB → PlayerDbSet
│   │   └── GameLogContext.cs        gamelog DB → LoginLog/ChatLog/RoomLog/StatLogDbSet
│   ├── DbSet/
│   │   ├── PlayerDbSet.cs           InsertAsync, UpdateLogoutAsync, GetMaxPlayerIdAsync
│   │   ├── LoginLogDbSet.cs         InsertAsync, UpdateLogoutAsync
│   │   ├── ChatLogDbSet.cs          InsertAsync (channel: "lobby:{id}" 또는 "room")
│   │   ├── RoomLogDbSet.cs          InsertAsync (action: enter/exit/disconnect), GetMaxRoomIdAsync
│   │   └── StatLogDbSet.cs          InsertAsync (player_count, created_at)
│   └── Rows/                        Dapper 매핑 DTO 5종 (PlayerRow, LoginLogRow, ChatLogRow, RoomLogRow, StatLogRow)
│
├── GameServer/
│   ├── Program.cs                   top-level statements 4줄
│   ├── AppConfig.cs                 IConfiguration 빌드 (JSON → 환경변수 → CLI 오버라이드 계층)
│   ├── appsettings.json             기본 설정 (포트, DB, API 키)
│   ├── appsettings.Docker.json      Docker 오버라이드 (Database.Host = "mysql")
│   ├── ServerStartup.cs             시스템 초기화 순서 조율 (SessionSystem → PlayerSystem → DB → Lobby → 서버 시작)
│   │
│   ├── Component/
│   │   ├── Player/
│   │   │   ├── PlayerComponent.cs       WorkerSystem 등록 엔티티, 패킷 라우팅, Disconnect 처리
│   │   │   ├── PlayerLobbyComponent.cs  CurrentLobby 상태, LobbyList/Chat/RoomEnter 처리
│   │   │   └── PlayerRoomComponent.cs   CurrentRoom 상태, Chat/Exit/Disconnect 처리
│   │   ├── Lobby/
│   │   │   └── LobbyComponent.cs        CAS 정원 예약, 플레이어/룸 관리, 브로드캐스트
│   │   └── Room/
│   │       └── RoomComponent.cs         CAS _state 상태 머신, 입장/퇴장/채팅, 방 자동 삭제
│   │
│   ├── Controllers/
│   │   ├── Base/
│   │   │   └── PlayerBaseController.cs  NewRouter() 공통 기반, Routes() 추상 메서드
│   │   ├── PlayerLobbyController.cs     ReqLobbyList, ReqLobbyChat, ReqRoomEnter
│   │   ├── PlayerRoomController.cs      ReqRoomChat, ReqRoomExit
│   │   └── PlayerHeartbeatController.cs ReqHeartbeat → ResHeartbeat
│   │
│   ├── Network/
│   │   ├── GameServerBootstrap.cs       DotNetty ServerBootstrap 설정 및 Bind
│   │   ├── GamePipelineInitializer.cs   파이프라인 구성 (프레임 코덱 + 핸들러)
│   │   ├── GameServerHandler.cs         ChannelActive/Inactive/ChannelRead0/ExceptionCaught
│   │   ├── LoginProcessor.cs            로그인 전체 흐름 (DB → SessionSystem → Lobby 배정)
│   │   ├── SessionComponent.cs          IChannel 래퍼, 패킷 큐, Interlocked 플래그 관리
│   │   ├── HeartbeatHandler.cs          IdleStateEvent 수신 → 채널 강제 종료
│   │   └── GamePacketExtensions.cs      packet.ExtractPayload() — oneof 타입/값 추출
│   │
│   ├── Systems/
│   │   ├── SessionSystem.cs             전용 스레드 + 이벤트 큐, 세션 수명주기 직렬 처리
│   │   ├── PlayerSystem.cs              WorkerSystem<PlayerComponent> (2워커), 플레이어 등록/제거
│   │   ├── LobbySystem.cs               로비 배열 관리, GetDefaultLobby 클러스터링
│   │   ├── IdGenerators.cs              UniqueIdGenerator × 3 (Player, Room, Lobby)
│   │   ├── UniqueIdGenerator.cs         Interlocked.Increment 기반 원자적 ID 발급
│   │   └── StatLogger.cs               60초 주기 접속자 수 → StatLogDbSet 비동기 기록
│   │
│   └── Web/
│       ├── WebServerHost.cs             Kestrel 호스트 빌더, Swagger(DEBUG/Development), 미들웨어 구성
│       ├── Dtos.cs                      DTO record 12종 (PlayerDto, LobbyDetailDto, RoomDetailDto 등)
│       ├── Middleware/
│       │   ├── ApiKeyMiddleware.cs      X-Api-Key 헤더 인증
│       │   ├── IpWhitelistMiddleware.cs AdminApi:AllowedIps 기반 IP 화이트리스트
│       │   └── RequestLoggingMiddleware.cs 요청/응답 Body 포함 로깅 (1KB truncate)
│       └── Controllers/
│           ├── HealthController.cs      GET /health
│           ├── LobbiesController.cs     GET /lobbies, POST /lobbies/{id}/broadcast
│           ├── RoomsController.cs       GET /rooms, GET /rooms/{id}, POST /rooms/{id}/broadcast
│           ├── PlayersController.cs     GET /players, POST /players/{id}/kick
│           ├── StatsController.cs       GET /stats, GET /stats/history
│           ├── ServerInfoController.cs  GET /server-info
│           ├── BroadcastController.cs   POST /broadcast (전체 공지)
│           └── AnalyticsController.cs   GET /analytics/chat-logs|login-logs|room-logs
│
├── GameClient/
│   ├── Program.cs                   단일/다중/reconnect-stress 루프 분기, EventLoopGroup 자동 스케일
│   ├── LoadTestConfig.cs            CLI 인자 파싱 (record)
│   ├── Network/
│   │   └── GameClientHandler.cs     클라이언트 채널 핸들러 (시나리오에 위임)
│   ├── Controllers/
│   │   └── ClientContext.cs         클라이언트 상태, Heartbeat 타이머, RoomEnter 재시도 스케줄
│   ├── Scenarios/
│   │   ├── ILoadTestScenario.cs     OnConnectedAsync / OnPacketReceivedAsync / OnDisconnected
│   │   ├── RoomScenario.cs          로그인 → 로비채팅 → 룸입장 → 채팅 → 퇴장
│   │   ├── RoomOnceScenario.cs      룸입장 → 채팅 → 퇴장 → 연결 종료
│   │   ├── RoomChatScenario.cs      룸입장 → --interval 간격으로 채팅 무한 반복
│   │   ├── RoomLoopScenario.cs      룸입장/퇴장 무한 반복 (2초 입장, 1초 대기)
│   │   ├── LobbyChatScenario.cs     로그인 → --interval 간격으로 로비채팅 무한 반복
│   │   └── ReconnectStressScenario.cs 접속→로그인→룸입장→채팅N회→퇴장→재접속 무한 반복
│   └── Stats/
│       └── LoadTestStats.cs         Interlocked 카운터 9종 (Sent/Recv/Errors/Connected/Disconnected/Reconnects/RoomCycles/ChatSent/ChatRecv)
│
├── db/
│   ├── schema_game.sql              gameserver DB DDL (players 테이블)
│   └── schema_log.sql               gamelog DB DDL (login_logs, chat_logs, room_logs, stat_logs)
│
├── Dockerfile                       멀티스테이지 빌드 (sdk:9.0 → aspnet:9.0)
├── docker-compose.yml               gameserver + mysql 컨테이너 구성
└── .dockerignore                    빌드 컨텍스트 제외 목록 (bin/, obj/)
```

---

## 기술적 도전 요약

| 도전 | 원인 | 해결책 |
|---|---|---|
| 룸 정원 초과 (TOCTOU) | `_players.Count`를 외부에서 읽어 낡은 값 참조 | `LobbyComponent._playerCount` CAS 예약 |
| Room 닫힘-예약 레이스 | `_reservedCount` + `_closing` 두 필드 분리 시 CAS 경쟁 | `_state` 단일 int 통합 (`-1`=닫히는 중) — 마지막 슬롯 반환을 단일 CAS로 처리 |
| Leave → Enter 레이스 | RoomController에서 Leave/Enter를 연달아 호출 | Leave 이벤트 내부에서 Enter 호출 (동일 워커 스레드 직렬화) |
| PlayerGameEnter 순서 역전 | `AttachPlayer`와 `PlayerSystem.Add` 사이 FIFO 미보장 | SessionSystem 이벤트 큐에 PlayerCreated → PlayerGameEnter 순서로 적재 |
| 로그인 중 연결 해제 | DB await 중 ChannelInactive 발화 → 정리 경로 누락 | `IsDisconnected` 체크 + `ImmediateFinalize()` / `DisconnectForNextTick()` 경로 분리 |
| DB 실패 시 유령 플레이어 | `players.Insert` 전에 로그인 응답 전송 | `await` 전환 + 실패 시 에러 응답 후 return |
| 비동기 예외 소실 | `_ = DbAsync()` 패턴이 예외를 삼킴 | `FireAndForget(tag)` 헬퍼로 예외 GameLogger 기록 |
| DotNetty void 경계 | `ChannelRead0/Inactive`가 `void` 반환 | 내부 async 체인 완성 후 경계에서만 `_ =` 처리 |
| 유령 연결 (Ghost Connection) | TCP keepalive가 수 시간 후에야 감지 | `IdleStateHandler(30s)` + 클라이언트 20초 Heartbeat 타이머 |
| 다중 클라이언트 static 상태 | 전역 `static ClientState`가 클라이언트 간 공유 | 인스턴스 기반 `ClientContext`로 전환 |
| 비인터랙티브 환경 즉시 종료 | stdin EOF 시 `Console.ReadLine()` 즉시 반환 | `CancellationTokenSource` + Ctrl+C 시그널 |
| `ContinueWith` 데드락 가능성 | `TaskScheduler.Current`가 I/O 스레드 스케줄러를 잡을 수 있음 | `TaskScheduler.Default` 명시 |
| ulong → long 오버플로우 | DB에서 읽은 `MAX(player_id)`를 long으로 캐스팅 | 상한 체크 + `ArgumentOutOfRangeException` |
| Broadcast Silent Failure | Room 종료 직후 `Broadcast()`가 false 반환 → 200 OK 응답 | 반환값 검사 후 404 반환 |
| ASP.NET Core + GameServer 통합 | SDK 충돌, `RunAsync(ct)` API 부재, `LogLevel` 네임스페이스 충돌 | FrameworkReference, `ct.Register(Lifetime.StopApplication)`, 전체 한정자 사용 |
| 설정 하드코딩 | 포트/DB 등 변경마다 재빌드 필요 | `IConfiguration` 빌더 — JSON / 환경변수 / CLI 오버라이드 계층 |
| PlayerSystem 좀비 잔류 | `Add` ↔ `session.Player` 사이 ChannelInactive 발화 → `DisconnectAsync` 미호출 | 순서 교체 + `channel.Active` 체크 + `Interlocked` 멱등성 가드 |
| Connection String Injection | DB 설정 직접 문자열 보간 — 특수문자 포함 시 파싱 깨짐 | `MySqlConnectionStringBuilder` 사용 |
| Room.Enter 실패 시 미아 상태 | catch 블록에서 Lobby 복구 누락 → 플레이어가 어디에도 속하지 않음 | catch에 `Lobby.TryEnter` 복귀 + 실패 응답 추가 |
| Thundering Herd (대규모 접속) | 1000명 동시 접속 시 연결 폭발 | `--delay N` CLI 옵션으로 클라이언트 접속 분산 + jitter 재접속 딜레이 |
| 재접속 루프 상태 오염 | 재접속 시 이전 세션 PlayerId/상태 잔류 | `ClientContext.ResetForReconnect()` 호출로 상태 초기화 |
| Common 프로젝트 결합도 | 서버/클라이언트 공용 `Common` 단일 프로젝트로 불필요한 의존성 발생 | `Common.Shared` / `Common.Server` / `Common.Client` 3분리 |
| MaxPlayers 이중 관리 | `GameServerSettings`와 `PlayerSystem`에 각각 상수 존재 | `Common.Server/Constants.cs` 단일 소스로 통합 |

---

## 실행 방법

### 사전 준비

```sql
-- MySQL에서 스키마 생성
source db/schema_game.sql;
source db/schema_log.sql;
```

`GameServer/appsettings.json` DB 접속 정보 및 API 키 설정:

```json
{
  "AdminApi": {
    "ApiKey": "CHANGE-THIS-SECRET-KEY",
    "AllowedIps": []
  },
  "GameServer": { "GamePort": 7777, "WebPort": 8080, "MaxPlayers": 100 },
  "Database": {
    "Host": "127.0.0.1", "Port": 3306,
    "UserId": "root", "Password": "",
    "Database": "gameserver", "LogDatabase": "gamelog",
    "RequireConnection": false
  }
}
```

> `AllowedIps`가 비어있으면 모든 IP에서 접근 허용. 값을 지정하면 해당 IP만 허용.
> Web API 요청 시 `X-Api-Key: <ApiKey>` 헤더 필수 (`/health` 제외).

환경변수 오버라이드: `GAMESERVER_Database__Password=mypassword`
명령행 오버라이드: `dotnet run --project GameServer -- --GameServer:MaxPlayers 500`

### 서버 실행

```bash
dotnet run --project GameServer
# 포트 7777 (게임 서버) + 포트 8080 (웹 서버) 동시 수신 대기
```

### 관리 웹 API

모든 요청에 `X-Api-Key` 헤더가 필요하다 (`/health` 제외).

```bash
API_KEY="CHANGE-THIS-SECRET-KEY"

# 헬스 체크 (인증 불필요)
curl http://localhost:8080/health

# 로비 목록
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/lobbies

# 룸 목록 / 룸 상세
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/rooms
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/rooms/1

# 특정 룸에 시스템 메시지 전송
curl -X POST http://localhost:8080/rooms/1/broadcast \
     -H "X-Api-Key: $API_KEY" \
     -H "Content-Type: application/json" \
     -d '{"message": "서버 점검 예정"}'

# 접속 중 플레이어 목록 / 강제 퇴장
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/players
curl -X POST -H "X-Api-Key: $API_KEY" http://localhost:8080/players/42/kick

# 서버 통계 / 접속자 시계열
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/stats
curl -H "X-Api-Key: $API_KEY" "http://localhost:8080/stats/history?limit=100"

# 전체 플레이어 시스템 공지
curl -X POST http://localhost:8080/broadcast \
     -H "X-Api-Key: $API_KEY" \
     -H "Content-Type: application/json" \
     -d '{"message": "전체 서버 공지입니다"}'

# 서버 정보
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/server-info

# 분석 로그 조회
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/analytics/chat-logs
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/analytics/login-logs
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/analytics/room-logs

# Swagger UI (DEBUG/Development 환경)
# http://localhost:8080/swagger
```

### 클라이언트 / 부하 테스트

```bash
# 기본 실행 (4클라이언트, room 시나리오)
dotnet run --project GameClient

# 주요 CLI 옵션
# --clients N          동시 접속 클라이언트 수 (기본 4)
# --scenario <name>    시나리오 (기본 room)
# --delay N            클라이언트당 접속 딜레이 ms (기본 10)
# --interval N         채팅 전송 간격 ms (기본 1000)
# --host <ip>          서버 호스트 (기본 127.0.0.1)
# --port N             서버 포트 (기본 7777)
# --prefix <str>       플레이어 이름 접두사 (기본 Bot)

# 시나리오 목록
# room             로그인 → 로비채팅 → 룸입장 → 채팅 → 퇴장
# room-once        룸입장 → 채팅 → 퇴장 → 연결 종료
# room-chat        룸입장 → --interval 간격 채팅 무한 반복
# room-loop        룸입장/퇴장 무한 반복
# lobby            로그인 → --interval 간격 로비채팅 무한 반복
# reconnect-stress 접속→로그인→룸→채팅→퇴장→재접속 무한 반복

# 예시: 100클라이언트 로비 채팅
dotnet run --project GameClient -- --clients 100 --scenario lobby --interval 500

# 예시: 1000클라이언트 재접속 스트레스 (접속 분산 필수)
dotnet run --project GameClient -- --clients 1000 --scenario reconnect-stress \
  --delay 5 --reconnect-delay 2000 --chat-count 3

# reconnect-stress 전용 옵션
# --reconnect-delay N  재접속 대기 ms (기본 2000, ±200ms jitter 자동 적용)
# --room-cycles N      목표 룸 사이클 수 (기본 0=무한)
# --chat-count N       룸 입장 후 채팅 전송 수 (기본 3)
```

### 부하 테스트 통계 (다중 클라이언트 시 5초 주기 출력)

```
Active=42 | Sent=12840 | Recv=11203 | ChatSent=4320 | ChatRecv=8640 | Errors=0 | Reconnects=980 | RoomCycles=490
```

---

## Docker 실행

Docker가 설치되어 있으면 로컬 환경 설정 없이 서버 + MySQL을 한 번에 띄울 수 있다.

```bash
# 빌드 후 실행 (gameserver + mysql)
docker compose up --build

# 백그라운드 실행
docker compose up --build -d

# 종료 (볼륨 유지)
docker compose down

# 종료 + 볼륨(DB 데이터) 삭제
docker compose down -v
```

**포트 노출**

| 포트 | 용도 |
|------|------|
| 7777 | 게임 서버 (TCP) |
| 8080 | 관리 Web API (HTTP) |
| 3306 | MySQL (호스트에서 직접 접근 가능) |

**설정 오버라이드**

Docker 환경에서는 `DOTNET_ENVIRONMENT=Docker`가 설정되어 `appsettings.Docker.json`이 자동 로드된다.
기본 `appsettings.json` 대비 변경되는 항목:

```json
{
  "Database": {
    "Host": "mysql",          // 컨테이너 서비스명으로 오버라이드
    "RequireConnection": true // DB 없으면 서버 시작 실패
  }
}
```

API 키 등 다른 설정은 환경변수로 오버라이드할 수 있다:

```bash
# docker-compose.yml의 environment 섹션에 추가하거나
GAMESERVER_AdminApi__ApiKey=my-secret-key docker compose up
```

**부하 테스트 (Docker 서버 대상)**

```bash
# 로컬에서 Docker 서버로 접속
dotnet run --project GameClient -- --host 127.0.0.1 --port 7777 --clients 100 --scenario room
```
