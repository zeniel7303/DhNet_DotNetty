# DotNetty 멀티플레이어 게임 서버

C++ 기반 게임 서버(DhNet)를 C# .NET 9 + DotNetty로 재이식한 프로젝트.
로비/룸 게임 로직, MySQL DB 레이어, REST API 웹 서버, 부하 테스트 인프라까지 갖춘 완성형 서버.

| 항목 | 내용 |
|------|------|
| **기술 스택** | C# .NET 9 · DotNetty 0.7.6 · Protocol Buffers · MySQL · Dapper · ASP.NET Core |
| **게임 포트** | TCP 7777 (DotNetty) |
| **관리 API** | HTTP 8080 (ASP.NET Core) |

---

## 설계 철학

이 프로젝트는 세 가지 질문에서 시작했다.

**1. 세션 수명주기와 패킷 처리를 어떻게 분리할 것인가?**

로그인 흐름은 `LoginProcessor`(TaskPool) → `SessionSystem`(전용 스레드 + 이벤트 큐) 순으로 직렬화한다.
세션이 완전히 등록된 이후에야 `WorkerSystem`이 패킷 처리를 시작하므로, 순서 역전 문제가 구조적으로 차단된다.

**2. 다중 스레드 환경에서 게임 상태를 lock 없이 관리할 수 있는가?**

`LobbyComponent`와 `RoomComponent`는 `Interlocked.CompareExchange`(CAS)로 정원을 예약한다.
`WorkerSystem`이 `InstanceId % workerCount`로 플레이어를 고정 워커에 배정해, 동일 플레이어의 패킷이 항상 단일 스레드에서 직렬 처리된다.

**3. DB 쓰기가 게임 루프를 블로킹하지 않도록 하려면?**

게임 상태(`players` 테이블)는 `await InsertAsync()`로 결과를 반드시 확인한다.
이벤트 로그(채팅·입퇴장·통계)는 `FireAndForget(tag)` 패턴으로 발사 — 실패 시 GameLogger에 기록한다.

---

## 아키텍처

```
[GameClient (n개 병렬)] ──TCP:7777──▶ DotNetty Pipeline
                                       ├─ LengthFieldPrepender / Decoder (2-byte 프레임)
                                       ├─ ProtobufEncoder / Decoder
                                       ├─ IdleStateHandler (30s) + HeartbeatHandler
                                       └─ GameServerHandler
                                            │
                         ┌─────────────────┴──────────────────┐
                   ReqLogin (직접 처리)              session.EnqueuePacket()
                   LoginProcessor (TaskPool)                  │
                         │                        PlayerComponent.Update (100ms tick)
                   SessionSystem                  DrainSessionPackets → RouterBuilder
                   (전용 스레드, 이벤트 큐)                    │
                   PlayerSystem 등록              ┌────────────┴────────────┐
                                       PlayerLobbyController   PlayerRoomController
                                                 │
                                       LobbyComponent / RoomComponent
                                       (CAS 기반 동시성 제어)
                                                 │
                              GameServer.Database (싱글톤)
                              ├── GameDbContext  → gameserver DB (핵심 데이터, await)
                              └── GameLogContext → gamelog DB   (로그, FireAndForget)

[Browser / curl] ──HTTP:8080──▶ ASP.NET Core REST API
                                 ├─ ApiKeyMiddleware + IpWhitelistMiddleware
                                 ├─ GET /lobbies, /rooms, /players, /stats
                                 ├─ POST /broadcast, /players/{id}/kick
                                 └─ GET /analytics/chat-logs|login-logs|room-logs
```

> **왜 로그인만 직접 처리하는가?** — 로그인 시점에는 아직 `PlayerComponent`가 생성되지 않았다. 세션만 존재하는 상태에서 DB 조회·플레이어 생성을 완료한 뒤에야 ECS 워커로 패킷 처리를 위임할 수 있다.

---

## 핵심 구현 포인트

### 1. GamePacket oneof 래퍼 — 단일 디코더로 17종 처리

```protobuf
message GamePacket {
  oneof payload {
    ReqLogin req_login = 1;  ResLogin res_login = 2;
    ReqRoomEnter req_room_enter = 3;  // ... 총 17종
  }
}
```

- **단일 디코더**: `ProtobufDecoder(GamePacket.Parser)` 하나로 모든 패킷 역직렬화.
- **타입 안전 라우팅**: `ExtractPayload()`가 `(Type, object)` 튜플을 반환 → `RouterBuilder`가 O(1) 디스패치.

> **왜 oneof인가?** — 메시지 타입마다 별도 디코더를 등록하면 파이프라인이 복잡해진다. oneof 래퍼는 단일 진입점을 유지하면서도 컴파일 타임 타입 안전성을 보장한다.

### 2. SessionSystem — 이벤트 큐로 수명주기 직렬화

```
[I/O EventLoop]    ChannelRead0(ReqLogin) → LoginProcessor.ProcessAsync()
[LoginProcessor]   DB await → EnqueuePlayerCreated → SessionSystem: AttachPlayer
(TaskPool)                  → EnqueuePlayerGameEnter → PlayerSystem.Add → TCS.SetResult
[WorkerSystem]     (TCS 완료 이후) 패킷 처리 시작
```

`SessionSystem` 전용 스레드가 `PlayerCreated → PlayerGameEnter` 순서를 보장한다.
`AttachPlayer`와 `PlayerSystem.Add` 간 순서 역전 문제가 구조적으로 차단된다.

### 3. WorkerSystem — 플레이어별 워커 고정으로 직렬성 보장

```csharp
// InstanceId % workerCount 로 동일 플레이어를 동일 워커에 고정
int index = (int)((item.InstanceId & long.MaxValue) % workerCount);
_workers[index].Add(item);

// 100ms 틱마다 세션 큐 전체 드레인
public override void Update(float dt) => DrainSessionPackets();
```

> 락 없이 패킷 처리가 단일 스레드에서 직렬 실행된다. 1 패킷 = 1 Task 방식의 컨텍스트 스위칭 비용을 배치 처리로 제거한다.

### 4. CAS 기반 무락 동시성

**LobbyComponent — 정원 예약:**
```csharp
do {
    current = _playerCount;
    if (current >= MaxCapacity) return false;
} while (Interlocked.CompareExchange(ref _playerCount, current + 1, current) != current);
```

**RoomComponent — `_state` 단일 int 통합 (`-1`=닫히는 중, `0~N`=예약 수):**
```csharp
int next = current == 1 ? -1 : current - 1;
if (Interlocked.CompareExchange(ref _state, next, current) == current)
    return next == -1;  // 마지막 반환자가 방 삭제 책임
```

> `_reservedCount` + `_closing` 두 필드를 분리하면 두 CAS 사이에 레이스가 생긴다. 단일 int로 통합하면 상태 전환이 원자적으로 처리된다.

### 5. LobbySystem — 클러스터링 로비 배정

`GetDefaultLobby()`는 가득 차지 않은 로비 중 **현재 인원이 가장 많은** 로비를 반환한다.
소수 접속 시 여러 로비에 분산되지 않고 한 로비로 클러스터링해 채팅 활성도를 유지한다.

### 6. DB 레이어 — 목적별 처리 분리

| DB | 용도 | 처리 방식 | 이유 |
|----|------|-----------|------|
| `gameserver` | 플레이어 상태 | `await InsertAsync()` | 데이터 무결성 보장 필수 |
| `gamelog` | 이벤트 로그 | `FireAndForget("tag")` | I/O 지연이 게임 루프를 블로킹하면 안 됨 |

- `MySqlConnectionStringBuilder`로 Connection String 조립 → Injection 벡터 차단.
- 서버 재시작 시 `MAX(player_id)` / `MAX(room_id)` 조회로 ID 카운터 이어받기.

---

## 프로젝트 구조

```
DhNet_DotNetty/
├── Common.Shared/       GameLogger (채널 기반 비동기), LogEntry, Helper
├── Common.Server/
│   ├── Component/       BaseComponent, BaseWorker, WorkerSystem
│   └── Routing/         IRouter, PacketRouter, RouterBuilder
├── GameServer.Protocol/ .proto 6개 → C# 자동 생성 (game_packet, login, lobby, room, heartbeat, system)
├── GameServer.Database/ DatabaseSystem, DbConnector, DbSet 5종, Row DTO 5종
├── GameServer/
│   ├── Component/       PlayerComponent, LobbyComponent, RoomComponent
│   ├── Controllers/     PlayerLobbyController, PlayerRoomController, PlayerHeartbeatController
│   ├── Network/         GameServerBootstrap, GamePipelineInitializer, GameServerHandler, LoginProcessor
│   ├── Systems/         SessionSystem, PlayerSystem, LobbySystem, StatLogger
│   └── Web/             WebServerHost, Middleware 3종, REST Controller 8종
├── GameClient/
│   ├── Scenarios/       room, room-once, room-chat, room-loop, lobby, reconnect-stress
│   └── Stats/           LoadTestStats (Interlocked 카운터 9종)
├── db/                  schema_game.sql, schema_log.sql
├── Dockerfile           멀티스테이지 빌드 (sdk:9.0 → aspnet:9.0)
└── docker-compose.yml   gameserver + mysql
```

---

## 실행 방법

### 사전 준비

```sql
source db/schema_game.sql;
source db/schema_log.sql;
```

`GameServer/appsettings.json` 설정:

```json
{
  "AdminApi": { "ApiKey": "CHANGE-THIS-SECRET-KEY", "AllowedIps": [] },
  "GameServer": { "GamePort": 7777, "WebPort": 8080, "MaxPlayers": 100 },
  "Database": { "Host": "127.0.0.1", "Port": 3306, "UserId": "root", "Password": "" }
}
```

> 설정은 JSON → 환경변수 → CLI 순으로 오버라이드된다.
> `GAMESERVER_Database__Password=pw` / `dotnet run -- --GameServer:MaxPlayers 500`

### 서버 실행

```bash
dotnet run --project GameServer
# TCP 7777 (게임) + HTTP 8080 (관리 API) 동시 수신 대기
```

### 관리 웹 API

```bash
API_KEY="CHANGE-THIS-SECRET-KEY"
curl http://localhost:8080/health                                         # 인증 불필요
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/lobbies
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/rooms
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/players
curl -H "X-Api-Key: $API_KEY" http://localhost:8080/stats
curl -X POST http://localhost:8080/players/42/kick -H "X-Api-Key: $API_KEY"
curl -X POST http://localhost:8080/broadcast \
     -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" \
     -d '{"message": "서버 공지"}'
```

### 부하 테스트

```bash
# 기본 (4클라이언트, room 시나리오)
dotnet run --project GameClient

# 주요 옵션
# --clients N   --scenario <name>   --delay N   --interval N
# --host <ip>   --port N            --prefix <str>

# 시나리오: room | room-once | room-chat | room-loop | lobby | reconnect-stress

# 예: 100클라이언트 로비 채팅
dotnet run --project GameClient -- --clients 100 --scenario lobby --interval 500

# 예: 1000클라이언트 재접속 스트레스
dotnet run --project GameClient -- --clients 1000 --scenario reconnect-stress \
  --delay 5 --reconnect-delay 2000 --chat-count 3
```

부하 테스트 통계 (5초 주기):
```
Active=42 | Sent=12840 | Recv=11203 | ChatSent=4320 | ChatRecv=8640 | Errors=0 | Reconnects=980 | RoomCycles=490
```

---

## Docker 실행

```bash
docker compose up --build          # 빌드 후 실행
docker compose up --build -d       # 백그라운드
docker compose down                # 종료 (볼륨 유지)
docker compose down -v             # 종료 + 볼륨 삭제
```

| 포트 | 용도 |
|------|------|
| 7777 | 게임 서버 (TCP) |
| 8080 | 관리 Web API (HTTP) |
| 3306 | MySQL |

Docker 환경에서는 `DOTNET_ENVIRONMENT=Docker`가 설정되어 `appsettings.Docker.json`(`Database.Host=mysql`)이 자동 로드된다.

---

개발 과정 상세 기록 → [DEVLOG.md](DEVLOG.md)
