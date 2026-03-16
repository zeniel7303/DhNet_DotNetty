# DotNetty 멀티플레이어 게임 서버

C++ 기반 게임 서버(DhNet)를 C# .NET 9 + DotNetty로 재이식한 프로젝트.
단순 이식에서 시작해 비동기 아키텍처 설계, 동시성 버그 수정, DB 레이어 통합, 부하 테스트 인프라, 관리용 REST API 웹 서버까지 단계적으로 발전시켰다.

**기술 스택**: C# .NET 9, DotNetty 0.7.6, Protocol Buffers, MySQL, Dapper, ASP.NET Core

---

## 목차

1. [전체 아키텍처](#전체-아키텍처)
2. [프로젝트 구조](#프로젝트-구조)
3. [기술적 도전 요약](#기술적-도전-요약)
4. [실행 방법](#실행-방법)

---

## 전체 아키텍처

```
[GameClient (n개 병렬)] ──TCP:7777──▶ [DotNetty Pipeline]
                                           LengthFieldBasedFrameDecoder
                                           LengthFieldPrepender
                                           ProtobufDecoder / ProtobufEncoder
                                           GameServerHandler
                                                │
                                          PacketRouter
                               ┌───────────────┴────────────────┐
                         LoginController           LobbyController / RoomController
                               │                            │
                            Lobby ◀────────────────▶ Room (최대 2인)
                       JobQueue(Channel)          JobQueue(Channel)
                            │                            │
                    ┌───────────────────────────────────────────┐
                    │       GameServer.Database (싱글톤)         │
                    │  ┌─ GameDbContext  → gameserver DB         │
                    │  │   └── PlayerDbSet                       │
                    │  └─ GameLogContext → gamelog DB             │
                    │      ├── LoginLogDbSet                      │
                    │      ├── ChatLogDbSet                       │
                    │      ├── RoomLogDbSet                       │
                    │      └── StatLogDbSet                       │
                    └───────────────────────────────────────────┘

[Browser / 관리 도구] ──HTTP:8080──▶ [ASP.NET Core Web Server]
                                           GET  /health
                                           GET  /lobby
                                           GET  /rooms
                                           POST /rooms/{id}/broadcast
                                                │ In-Process
                                          PlayerSystem / LobbySystem / Room
```

### 핵심 설계 원칙

**JobQueue 기반 단일 스레드 보장**
Lobby와 Room은 각각 독립적인 `Channel<Func<Task>>` JobQueue를 보유한다. DotNetty I/O 스레드와 게임 로직을 분리하면서, 엔티티 내부는 순차 실행을 보장한다.

```csharp
// 외부(I/O 스레드)에서 호출 — 즉시 반환
public void Chat(Player sender, string message) => DoAsync(async () =>
{
    // JobQueue 내부 — 단일 스레드 순차 실행
    await Task.WhenAll(_players.Select(p => p.Session.SendAsync(noti)));
});

private void DoAsync(Func<Task> job) => _jobChannel.Writer.TryWrite(job);
```

**GamePacket oneof 래퍼**
12종 패킷을 단일 타입으로 래핑해 ProtobufDecoder/Encoder 1쌍으로 처리한다.

```protobuf
message GamePacket {
  oneof payload {
    ReqLogin       req_login        = 1;
    ResLogin       res_login        = 2;
    ReqLobbyChat   req_lobby_chat   = 3;
    NotiLobbyChat  noti_lobby_chat  = 4;
    ReqRoomEnter   req_room_enter   = 5;
    ResRoomEnter   res_room_enter   = 6;
    NotiRoomEnter  noti_room_enter  = 7;
    ReqRoomChat    req_room_chat    = 8;
    NotiRoomChat   noti_room_chat   = 9;
    ReqRoomExit    req_room_exit    = 10;
    ResRoomExit    res_room_exit    = 11;
    NotiRoomExit   noti_room_exit   = 12;
    ReqHeartbeat   req_heartbeat    = 13;
    ResHeartbeat   res_heartbeat    = 14;
  }
}
```

---

## 프로젝트 구조

```
DotNetty/
├── Common/
│   ├── Logging/              GameLogger, LogEntry, LogLevel
│   ├── GameServerSettings.cs GamePort(7777), WebPort(8080), MaxPlayers
│   └── DatabaseSettings.cs  Host/Port/Database/RequireConnection 등
├── GameServer.Protocol/      .proto 4개 → C# 클래스 자동 생성 (Grpc.Tools)
├── GameServer.Database/      MySQL + Dapper DB 레이어
│   ├── DatabaseSystem.cs     싱글톤, GameDbContext + GameLogContext 관리
│   ├── DbExtensions.cs       FireAndForget 확장 메서드
│   ├── System/               DbConnector (Dapper 래퍼), 두 Context 클래스
│   ├── DbSet/                PlayerDbSet, LoginLogDbSet, ChatLogDbSet, RoomLogDbSet, StatLogDbSet
│   └── Rows/                 Dapper 매핑용 Row DTO 5종
├── GameServer/
│   ├── Program.cs            4줄 top-level statements
│   ├── ServerStartup.cs      DB 초기화 + IdGenerators + CancellationToken + 서버 시작 조율
│   ├── Network/              GameSession, GameServerBootstrap, GamePipelineInitializer, GameServerHandler
│   ├── Controllers/          PacketRouter, LoginController, LobbyController, RoomController
│   ├── Entities/             Player, Lobby, Room
│   ├── Systems/              PlayerSystem, LobbySystem, GameSessionSystem, IdGenerators, StatLogger
│   └── Web/                  ASP.NET Core 관리 웹 서버 (port 8080)
│       ├── WebServerHost.cs  호스트 빌더 + CancellationToken 연동
│       ├── Dtos.cs           HealthDto, LobbyDto, RoomDto, BroadcastBody
│       └── Controllers/      HealthController, LobbyController, RoomsController
├── GameClient/
│   ├── Scenarios/            ILoadTestScenario, RoomScenario, LobbyChatScenario
│   ├── Network/              GameClientHandler
│   └── Controllers/          ClientContext, PacketRouter
├── db/
│   ├── schema_game.sql       gameserver DB DDL (players)
│   └── schema_log.sql        gamelog DB DDL (login_logs, room_logs, chat_logs, stat_logs)
└── Legacy/                   EchoServer, EchoClient, MemoryPack/MessagePack 직렬화 실험
```

---


## 기술적 도전 요약

| 도전 | 원인 | 해결책 |
|---|---|---|
| 룸 정원 초과 (TOCTOU) | `_players.Count`를 JobQueue 외부에서 읽어 낡은 값 참조 | `_reservedCount` + `Interlocked` 예약 카운터 |
| Leave → Enter 레이스 | `RoomController`에서 Leave/Enter를 연달아 호출 | `Leave` JobQueue 람다 내부에서 `Enter` 호출 |
| 비동기 예외 소실 | `_ = DbAsync()` 패턴이 예외를 삼킴 | `FireAndForget(tag)` 헬퍼로 예외 로깅 |
| DB 실패 시 유령 플레이어 | `players.Insert` 전에 로그인 응답 전송 | `await` 전환 + 실패 시 에러 응답 후 return |
| DotNetty void 경계 | `ChannelRead0/Inactive`가 `void` 반환 | 내부 async 체인 완성 후 경계에서만 `_ =` 처리 |
| 다중 클라이언트 static 상태 | 전역 `static ClientState`가 클라이언트 간 공유 | 인스턴스 기반 `ClientContext`로 전환 |
| 비인터랙티브 환경 즉시 종료 | stdin EOF 시 `Console.ReadLine()` 즉시 반환 | `CancellationTokenSource` + Ctrl+C 시그널 |
| `ContinueWith` 데드락 가능성 | `TaskScheduler.Current`가 I/O 스레드 스케줄러를 잡을 수 있음 | `TaskScheduler.Default` 명시 |
| ulong → long 오버플로우 | DB에서 읽은 `MAX(player_id)`를 long으로 캐스팅 | 상한 체크 + `ArgumentOutOfRangeException` |
| Broadcast Silent Failure | Room 종료 직후 `TryWrite`가 false 반환 → 200 OK 응답 | `bool Broadcast()` 반환값 검사 후 404 반환 |
| ASP.NET Core + GameServer 통합 | SDK 충돌, `RunAsync(ct)` API 부재, `LogLevel` 네임스페이스 충돌 | FrameworkReference, `ct.Register(Lifetime.StopApplication)`, 전체 한정자 사용 |
| 유령 연결 (Ghost Connection) | TCP keepalive가 수 시간 후에야 감지, 강제 종료 클라이언트가 리소스 점유 | `IdleStateHandler(30s)` + 클라이언트 20초 Heartbeat 타이머 |
| 설정 하드코딩 | 포트/DB 등 변경마다 재빌드 필요 | `IConfiguration` 빌더 — JSON / 환경변수 / 커맨드라인 오버라이드 계층 |
| PlayerSystem 좀비 잔류 | `Add` ↔ `session.Player` 사이 ChannelInactive 발화 → `DisConnectAsync` 미호출 | 순서 교체 + `channel.Active` 체크 + `DisConnectAsync` `Interlocked` 멱등성 가드 |
| Connection String Injection | DB 설정 직접 문자열 보간 — 특수문자 포함 시 파싱 깨짐 | `MySqlConnectionStringBuilder` 사용 |
| Room.Enter 실패 시 미아 상태 | catch 블록에서 Lobby 복구 누락 → 플레이어가 어디에도 속하지 않음 | catch에 `Lobby.Enter` + `successSent` 플래그 기반 실패 응답 추가 |

---

## 실행 방법

### 사전 준비

```sql
-- MySQL에서 스키마 생성
source db/schema_game.sql;
source db/schema_log.sql;
```

`GameServer/appsettings.json`에서 DB 접속 정보 설정 (또는 환경변수로 오버라이드):

```json
{
  "GameServer": { "GamePort": 7777, "WebPort": 8080, "MaxPlayers": 100 },
  "Database":   { "Host": "127.0.0.1", "Port": 3306, "UserId": "root",
                  "Password": "", "Database": "gameserver", "LogDatabase": "gamelog" }
}
```

환경변수 오버라이드 예시: `GAMESERVER_Database__Password=mypassword`

### 서버 실행

```bash
dotnet run --project GameServer
# 포트 7777 (게임 서버) + 포트 8080 (웹 서버) 동시 수신 대기
```

### 관리 웹 API

```bash
# Swagger UI
http://localhost:8080/swagger

# 헬스 체크
curl http://localhost:8080/health

# 로비 플레이어 수
curl http://localhost:8080/lobby

# 룸 목록
curl http://localhost:8080/rooms

# 특정 룸에 시스템 메시지 전송
curl -X POST http://localhost:8080/rooms/1/broadcast \
     -H "Content-Type: application/json" \
     -d '{"message": "서버 점검 예정"}'
```

### 클라이언트 / 부하 테스트

```bash
# 단일 클라이언트
dotnet run --project GameClient

# 부하 테스트 (50클라이언트, 룸 시나리오, 1초 간격)
dotnet run --project GameClient -- --clients 50 --scenario room --interval 1000

# 로비 채팅 부하 테스트
dotnet run --project GameClient -- --clients 100 --scenario lobby --interval 500
```
