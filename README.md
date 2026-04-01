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

**4. "워커 스레드에서 완료될 때까지 기다려야 한다"를 lock 없이 어떻게 표현하는가?**

`EnqueueEvent`는 fire-and-forget이다. 완료를 기다려야 하는 경우(예: 로그인 흐름에서 `PlayerGameEnter` 완료 확인)에는 `TaskCompletionSource`를 사용한다.

```csharp
// EnqueueEventAsync: 워커 스레드에서 실행 완료 시 tcs.TrySetResult() 신호
await session.EnqueueEventAsync(() => { /* 상태 변경 */ });
// 여기서부터는 워커 틱에서 처리가 확실히 완료됨
```

워커 스레드가 job을 처리하면서 TCS를 완료시키면 await가 재개된다. 크로스 스레드 완료 통지를 lock 없이 표현하는 방식이다.
컴포넌트가 disposed된 경우 TCS를 즉시 취소(`TrySetCanceled`)해 호출자가 무한 대기하지 않도록 한다.

---

## 아키텍처

```
[GameClient (n개 병렬)] ──TCP:7777──▶ DotNetty Pipeline
                                       ├─ LengthFieldPrepender / Decoder (2-byte 프레임)
                                       ├─ AesGcmEncryptionHandler / DecryptionHandler (AES-128-GCM)
                                       ├─ ProtobufEncoder / Decoder
                                       ├─ IdleStateHandler (30s) + HeartbeatHandler
                                       └─ GameServerHandler
                                            │
                         ┌─────────────────┴──────────────────┐
                   ReqRegister/ReqLogin (직접 처리)   session.EnqueuePacket()
                   Register/LoginProcessor (TaskPool)         │
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
                                 ├─ POST /broadcast, /players/{id}/kick, /shutdown
                                 └─ GET /analytics/chat-logs|login-logs|room-logs
```

> **왜 회원가입/로그인만 직접 처리하는가?** — 이 시점에는 아직 `PlayerComponent`가 생성되지 않았다. 세션만 존재하는 상태에서 DB 조회·계정 인증·플레이어 생성을 완료한 뒤에야 ECS 워커로 패킷 처리를 위임할 수 있다.

---

## 핵심 구현 포인트

### 1. 접속부터 게임 입장까지의 플로우

```
클라이언트                          서버 (LoginProcessor, TaskPool)
    │                                │
    │  ── TCP 연결 ──────────────────▶│  AES-GCM 암호화 채널 수립
    │                                │
    │  ① 회원가입
    │──── ReqRegister ───────────────▶│  username/password 4~16자 검증
    │                                │  INSERT IGNORE (UNIQUE 제약, 중복 안전)
    │◀─── ResRegister ───────────────│  SUCCESS(신규) or USERNAME_TAKEN(기존)
    │                                │
    │  ② 로그인
    │──── ReqLogin ──────────────────▶│  길이 검증 → SELECT by username
    │                                │  password 비교 → 실패 시 INVALID_CREDENTIALS
    │                                │  성공 시:
    │                                │  ├─ DB: players INSERT (login_at, ip, account_id)
    │                                │  ├─ EnqueuePlayerCreated
    │                                │  │    └▶ SessionSystem: session.Player = player
    │                                │  ├─ EnqueuePlayerGameEnter
    │                                │  │    └▶ PlayerSystem.Add (WorkerSystem 배정)
    │                                │  └─ LobbySystem.GetDefaultLobby → TryEnter
    │◀─── ResLogin ──────────────────│  PlayerId + PlayerName + SUCCESS
    │                                │  (이 시점에 이미 로비 입장 완료)
    │                                │
    │  ③ 로비 활동
    │──── ReqLobbyChat ──────────────▶│  WorkerSystem → PlayerLobbyController
    │◀─── NotiLobbyChat (브로드캐스트)│  로비 내 전체 전송
    │                                │
    │  ④ 룸 입장
    │──── ReqRoomEnter ──────────────▶│  LobbyComponent → RoomComponent.TryEnter (CAS)
    │◀─── ResRoomEnter ──────────────│  SUCCESS or LOBBY_FULL(재시도)
    │◀─── NotiRoomEnter (브로드캐스트)│  룸 내 전체 전송
    │                                │
    │  ⑤ 룸 활동
    │──── ReqRoomChat ───────────────▶│  WorkerSystem → PlayerRoomController
    │◀─── NotiRoomChat (브로드캐스트) │  룸 내 전체 전송
    │                                │
    │  ⑥ 룸 퇴장 / 연결 종료
    │──── ReqRoomExit ───────────────▶│  RoomComponent.TryLeave (CAS)
    │◀─── ResRoomExit ───────────────│
    │◀─── NotiRoomExit (브로드캐스트) │
    │                                │  DisconnectAsync:
    │  ── TCP 해제 ──────────────────▶│  ├─ DB: players UPDATE (logout_at)
    │                                │  └─ gamelog: login_logs INSERT (FireAndForget)
```

**핵심 설계 포인트**

- **ResLogin은 로비 입장 완료 후 전송** — 클라이언트가 패킷을 받는 순간 이미 로비 상태. 별도 타이밍 처리 불필요
- **봇 재접속** — `ReqRegister` → `USERNAME_TAKEN` → `ReqLogin` 순서로 신규/기존 계정 모두 동일 처리
- **이름 신뢰성** — 클라이언트 입력값 무시, DB의 `accounts.username` 값 사용 (조작 불가)
- **User Enumeration 방지** — username 없음과 password 불일치 모두 `INVALID_CREDENTIALS` 동일 응답
- **중복 로그인 차단** — DB Insert 전 `TryReserveLogin`으로 account_id 원자적 예약 → 중복 시 `ALREADY_LOGGED_IN(1006)` 응답
- **워커 고정** — `PlayerGameEnter` 이후 `InstanceId % workerCount`로 동일 플레이어 패킷이 항상 단일 스레드에서 처리

#### 에러 코드

| 코드 | 값 | 발생 시점 |
|------|----|----------|
| `SUCCESS` | 0 | 성공 |
| `SERVER_FULL` | 1000 | ReqLogin — 서버 정원 초과 |
| `DB_ERROR` | 1001 | ReqRegister/ReqLogin — DB 실패 |
| `INVALID_USERNAME_LENGTH` | 1002 | ReqRegister — username 4~16자 위반 |
| `INVALID_PASSWORD_LENGTH` | 1003 | ReqRegister — password 4~16자 위반 |
| `USERNAME_TAKEN` | 1004 | ReqRegister — 중복 username (로그인 진행) |
| `INVALID_CREDENTIALS` | 1005 | ReqLogin — username/password 불일치 |
| `ALREADY_LOGGED_IN` | 1006 | ReqLogin — 이미 로그인 중인 계정 |
| `LOBBY_FULL` | 2000 | ReqLogin/ReqRoomEnter — 배정 가능한 로비/룸 없음 |
| `NOT_IN_LOBBY` | 2001 | ReqRoomEnter — 로비 미입장 상태 |
| `ALREADY_IN_ROOM` | 3000 | ReqRoomEnter — 이미 룸에 입장한 상태 |

---

### 2. AES-128-GCM 패킷 암호화

모든 패킷을 AES-128-GCM으로 암호화한다. 프레이밍 이후, 직렬화 이전에 위치한다.

```
wire: [2B length] [12B nonce] [N bytes ciphertext] [16B auth-tag]
```

- **알고리즘**: AES-128-GCM — 암호화(기밀성) + 무결성 검증(변조 감지)을 단일 패스로 처리
- **Nonce**: 패킷마다 `RandomNumberGenerator`로 12바이트 생성 → Nonce 재사용 방지
- **키 방식**: Pre-shared key — `appsettings.json`의 `Encryption.Key`(Base64 16바이트)
- **비활성화**: `Encryption.Key`를 빈 문자열로 설정하면 핸들러가 파이프라인에서 제외됨
- **오버헤드**: 로컬 1000 클라이언트 기준 약 12% 처리량 감소 (377K vs 429K 패킷/s)

> **왜 AES-128이지 AES-256이 아닌가?** — 128-bit 키는 현존 양자컴퓨터로도 해독 불가 수준이다. 게임 서버에서 256-bit는 연산 비용만 늘린다.

### 3. GamePacket oneof 래퍼 — 단일 디코더로 19종 처리

```protobuf
message GamePacket {
  oneof payload {
    ReqLogin req_login = 1;  ResLogin res_login = 2;
    ReqRoomEnter req_room_enter = 3;  // ... 총 19종 (ReqRegister/ResRegister 포함)
  }
}
```

- **단일 디코더**: `ProtobufDecoder(GamePacket.Parser)` 하나로 모든 패킷 역직렬화.
- **타입 안전 라우팅**: `ExtractPayload()`가 `(Type, object)` 튜플을 반환 → `RouterBuilder`가 O(1) 디스패치.

> **왜 oneof인가?** — 메시지 타입마다 별도 디코더를 등록하면 파이프라인이 복잡해진다. oneof 래퍼는 단일 진입점을 유지하면서도 컴파일 타임 타입 안전성을 보장한다.

### 4. SessionSystem — 이벤트 큐로 수명주기 직렬화

```
[I/O EventLoop]    ChannelRead0(ReqLogin) → LoginProcessor.ProcessAsync()
[LoginProcessor]   DB await → EnqueuePlayerCreated → SessionSystem: AttachPlayer
(TaskPool)                  → EnqueuePlayerGameEnter → PlayerSystem.Add → TCS.SetResult
[WorkerSystem]     (TCS 완료 이후) 패킷 처리 시작
```

`SessionSystem` 전용 스레드가 `PlayerCreated → PlayerGameEnter` 순서를 보장한다.
`AttachPlayer`와 `PlayerSystem.Add` 간 순서 역전 문제가 구조적으로 차단된다.

### 5. WorkerSystem — 플레이어별 워커 고정으로 직렬성 보장

```csharp
// InstanceId % workerCount 로 동일 플레이어를 동일 워커에 고정
int index = (int)((item.InstanceId & long.MaxValue) % workerCount);
_workers[index].Add(item);

// 100ms 틱마다 세션 큐 전체 드레인
public override void Update(float dt) => DrainSessionPackets();
```

> 락 없이 패킷 처리가 단일 스레드에서 직렬 실행된다. 1 패킷 = 1 Task 방식의 컨텍스트 스위칭 비용을 배치 처리로 제거한다.

### 6. CAS 기반 무락 동시성

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

### 7. LobbySystem — 클러스터링 로비 배정

`GetDefaultLobby()`는 가득 차지 않은 로비 중 **현재 인원이 가장 많은** 로비를 반환한다.
소수 접속 시 여러 로비에 분산되지 않고 한 로비로 클러스터링해 채팅 활성도를 유지한다.

### 8. DB 레이어 — 목적별 처리 분리

| DB | 용도 | 처리 방식 | 이유 |
|----|------|-----------|------|
| `gameserver` | 플레이어 상태 | `await InsertAsync()` | 데이터 무결성 보장 필수 |
| `gamelog` | 이벤트 로그 | `FireAndForget("tag")` | I/O 지연이 게임 루프를 블로킹하면 안 됨 |

- `MySqlConnectionStringBuilder`로 Connection String 조립 → Injection 벡터 차단.
- 서버 재시작 시 `MAX(player_id)` / `MAX(room_id)` 조회로 ID 카운터 이어받기.

---

## 게임 콘텐츠

Vampire Survivors 스타일 멀티플레이어 탑다운 슈터. 브라우저(Canvas 2D + WebSocket)로 플레이.

### 게임 목표

50웨이브를 생존해 최종 보스 Reaper를 처치하면 클리어. 웨이브는 8초 간격으로 자동 시작되며, 플레이어는 경험치 젬 수집으로 레벨업하고 스탯 업그레이드를 선택한다.

### 무기 (6종)

| WeaponId | 이름 | 이모지 | 동작 방식 |
|----------|------|--------|----------|
| 0 | 마늘 | — | 플레이어 주변 AoE 오라 — 범위 내 적에게 지속 데미지 + 넉백 |
| 1 | 마법 지팡이 | 🪄 | 최근접 적 자동 조준 직선 투사체 (기본 시작 무기) |
| 2 | 성경 | 📖 | 플레이어 주위 궤도 공전, 접촉 적에게 지속 데미지 |
| 3 | 도끼 | 🪓 | 중력 포물선 투사체 (관통), 진행 방향 이미지 반전 |
| 4 | 단검 | 🗡️ | 이동 방향으로 발사되는 직선 투사체 |
| 5 | 십자가 | ✝️ | sin 궤적 왕복 부메랑, 관통, 전진·귀환 각 1회 명중 |

### 웨이브 구조 (50스테이지)

| 구간 | 내용 |
|------|------|
| Wave 1-5 | 고정 테이블 — Bat / Zombie / Skeleton / Ghost / GiantZombie 순차 등장 |
| Wave 6-49 | 동적 스케일링 — Bat(최대 40) · Zombie(최대 20) · Skeleton · Ghost 혼합, 5배수마다 GiantZombie |
| Wave 50 | **최종 보스 Reaper 등장** — 처치 시 스테이지 클리어 |
| 시간 초과 | 1800초(30분) 생존 시 자동 클리어 |

**몬스터 스탯 스케일링**: Wave마다 HP/ATK 8% 증가 (Wave 1=1.0x, Wave 10≈1.7x, Wave 50≈4.9x). 최대 동시 몬스터 500마리.

### 몬스터 (9종)

| 이름 | 기본 HP | 기본 ATK | 속도 | 특징 |
|------|---------|---------|------|------|
| Bat | 10 | 5 | 120 | 고속, 낮은 체력 |
| Zombie | 50 | 8 | 40 | 느리지만 다수 등장 |
| Skeleton | 80 | 12 | 60 | 방어력 2 |
| Ghost | 60 | 10 | 100 | 고속 유령 |
| GiantZombie | 300 | 30 | 25 | 미니 보스, 5웨이브마다 등장 |
| Reaper | 500 | 50 | 80 | 최종 보스, Wave 50 단독 등장 |

### 경험치 젬 시스템

- 몬스터 처치 시 경험치 젬(💎) 드랍. 젬은 서버에서 위치 관리.
- 플레이어가 획득 반경(기본 50px, 업그레이드 최대 +120px)에 진입하면 자동 수집.
- 수집 시 클라이언트에서 젬이 플레이어 위치로 600ms 동안 부드럽게 이동하는 흡수 애니메이션 재생 (서버와 느슨한 동기화).
- 레벨업 시 HP/ATK/DEF/이동속도/획득반경/경험치배율 중 랜덤 선택지 표시.

---

## 프로젝트 구조

```
DhNet_DotNetty/
├── Common.Shared/       GameLogger (채널 기반 비동기), LogEntry, Helper
│   └── Crypto/          AesGcmCryptor (AES-128-GCM 암호화/복호화)
├── Common.Server/
│   ├── Component/       BaseComponent, BaseWorker, WorkerSystem
│   └── Routing/         IRouter, PacketRouter, RouterBuilder
├── GameServer.Protocol/ .proto 7개 → C# 자동 생성 (game_packet, login, register, lobby, room, heartbeat, system)
├── GameServer.Database/ DatabaseSystem, DbConnector, DbSet 5종, Row DTO 5종
├── GameServer/
│   ├── Component/       PlayerComponent, LobbyComponent, RoomComponent
│   ├── Controllers/     PlayerLobbyController, PlayerRoomController, PlayerHeartbeatController
│   ├── Network/         GameServerBootstrap, GamePipelineInitializer, GameServerHandler, LoginProcessor
│   │                    AesGcmDecryptionHandler, AesGcmEncryptionHandler
│   │   └── Policies/    IPacketPolicy, PacketPairPolicy, PacketRatePolicy, PacketHandshakePolicy
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
  "GameServer": { "GamePort": 7777, "WebPort": 8080 },
  "Database": { "Host": "127.0.0.1", "Port": 3306, "UserId": "root", "Password": "0000" },
  "Encryption": { "Key": "AiZROpbIadx1uVbp64v7nQ==" }
}
```

> `Encryption.Key`는 Base64 인코딩된 16바이트 키다. 빈 문자열(`""`)로 설정하면 암호화가 비활성화된다.
> 새 키 생성: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))`

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
curl -X POST http://localhost:8080/shutdown -H "X-Api-Key: $API_KEY"  # Graceful Shutdown (202)
```

### 부하 테스트

```bash
# 기본 (1000클라이언트, lobby-chat 시나리오)
dotnet run --project GameClient

# 주요 옵션
# --clients N   --scenario <name>   --delay N   --interval N
# --host <ip>   --port N            --prefix <str>
# --encryption-key <base64>   (생략 시 서버 기본값과 동일한 키 사용)

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
