# RPG 웹게임 - Context

Last Updated: 2026-03-24

---

## 요구사항 요약

- 기존 DotNetty + Protocol Buffers 유지
- 로비/룸 시스템 **유지** — 유저가 룸을 직접 생성하고 입장
- 멀티플레이어: **PvE 전용** (플레이어 간 전투 없음)
- 룸당 최대 **2인**
- 브라우저에서 플레이 가능한 간단한 웹 RPG

---

## 핵심 파일 목록

### 재활용 (수정 없거나 최소 수정)
- `GameServer/Network/GameServerBootstrap.cs` — DotNetty 서버 부트스트랩
- `GameServer/Network/GamePipelineInitializer.cs` — **수정 필요**: WebSocket 핸들러 추가
- `GameServer/Network/SessionComponent.cs` — 세션 상태
- `GameServer/Network/GameServerHandler.cs` — **수정 필요**: RPG 패킷 라우팅 추가
- `GameServer/Network/LoginProcessor.cs` — **수정 필요**: CharacterComponent 초기화 연동
- `GameServer/Network/RegisterProcessor.cs` — 재활용
- `GameServer/Network/HeartbeatHandler.cs` — 재활용
- `GameServer/Systems/PlayerSystem.cs` — 재활용
- `GameServer/Systems/LobbySystem.cs` — 재활용 (수정 없음)
- `GameServer/Systems/ShutdownSystem.cs` — 재활용
- `GameServer/Systems/GameSystems.cs` — **수정 필요**: MonsterSystem, CombatSystem 추가
- `GameServer/Component/Lobby/LobbyComponent.cs` — 재활용 (수정 없음)
- `GameServer/Component/Player/PlayerLobbyComponent.cs` — 재활용 (수정 없음)
- `Common.Server/Component/WorkerSystem.cs` — 재활용
- `Common.Server/Routing/PacketRouter.cs` — 재활용
- `GameServer.Database/` — 전체 재활용 + CharacterDbSet 추가

### 수정 대상
- `GameServer/Component/Room/RoomComponent.cs` — 최대 인원 2 제한, 게임 시작 콜백
- `GameServer/Component/Player/PlayerRoomComponent.cs` — 게임 플레이 중 상태 연동
- `GameServer/Controllers/PlayerRoomController.cs` — ReqReadyGame 핸들러 추가
- `GameServer.Protocol/Protos/room.proto` — ReqReadyGame, NotiGameStart, NotiGameEnd 추가

### 신규 작성
- `GameServer/Network/WebSocketFrameHandler.cs` — WebSocket 바이너리 프레임 처리
- `GameServer/Component/Player/PlayerWorldComponent.cs` — 위치/이동/전투상태
- `GameServer/Component/Player/CharacterComponent.cs` — 스탯/레벨/EXP
- `GameServer/Component/GameSession/GameSessionComponent.cs` — 룸 1:1 게임 세션 (max 2인)
- `GameServer/Component/Monster/MonsterComponent.cs` — 몬스터 엔티티
- `GameServer/Systems/MonsterSystem.cs` — 몬스터 AI + 리스폰
- `GameServer/Systems/CombatSystem.cs` — PvE 전투 계산
- `GameServer/Controllers/PlayerRpgController.cs` — RPG 패킷 핸들러
- `GameServer.Protocol/Protos/world.proto` — 이동/게임세션 패킷
- `GameServer.Protocol/Protos/combat.proto` — PvE 전투 패킷
- `GameServer.Protocol/Protos/character.proto` — 캐릭터 정보 패킷
- `GameServer.Protocol/Protos/chat.proto` — 채팅 패킷
- `GameServer.Database/DbSet/CharacterDbSet.cs` — 캐릭터 DB 접근
- `GameServer.Database/Rows/CharacterRow.cs` — 캐릭터 DB 행
- `GameClient.Web/` — HTML5 웹 클라이언트 프로젝트 (신규)

---

## 핵심 설계 결정

### 1. WebSocket + Protobuf 프레이밍
DotNetty WebSocket 지원은 `DotNetty.Codecs.Http` 패키지에 포함됨.
파이프라인 구성:
```
HttpServerCodec
HttpObjectAggregator
WebSocketServerProtocolHandler (path: /ws)
WebSocketFrameHandler (BinaryWebSocketFrame -> ByteBuf)
LengthFieldPrepender(2) / LengthFieldBasedFrameDecoder
ProtobufDecoder / ProtobufEncoder
AesGcmDecryption/Encryption (optional)
HeartbeatHandler
GameServerHandler
```

### 2. 룸 기반 게임 세션 구조
- 기존 RoomComponent 유지, 최대 인원 2로 제한
- 게임 시작 시 GameSessionComponent 생성 (RoomComponent와 1:1)
- GameSessionComponent: 해당 룸의 몬스터/플레이어 위치 관리, 브로드캐스트
- 게임 종료 시 GameSessionComponent 해제, 플레이어는 로비로 복귀

### 3. PvE 전용 전투 모델
- 클릭 -> ReqAttack(targetMonsterId) — 플레이어 타겟 없음
- 서버: 사거리 체크 -> 데미지 계산 (ATK - DEF, 최소 1) -> NotiCombat 브로드캐스트
- 몬스터 HP 0 -> NotiDeath -> EXP 분배 (룸 내 2인 균등) -> NotiExpGain -> 레벨업 체크
- 몬스터 리스폰: 사망 후 N초 타이머 (종별 상이)
- 몬스터 -> 플레이어 자동 공격 (MonsterSystem AI)

### 4. 서버 권위 이동
- 클라이언트: ReqMove(targetX, targetY) 전송
- 서버: 유효성 검사 (이동 속도 초과 여부) 후 위치 업데이트
- NotiMove를 룸의 모든 플레이어에게 브로드캐스트
- 클라이언트: 보간(interpolation)으로 부드러운 움직임 표현

### 5. 캐릭터 데이터 저장 타이밍
- 로그인: DB에서 characters 레코드 로드 (없으면 기본값으로 생성)
- 로그아웃/연결 끊김: characters 레코드 업데이트 (HP, 위치, 레벨, EXP)
- 레벨업: 즉시 DB 업데이트 (데이터 손실 방지)

### 6. 게임 종료 조건
- **전멸**: 룸 내 모든 플레이어 HP 0 -> NotiGameEnd(result=Defeat)
- **클리어**: 보스(Dragon) 처치 -> NotiGameEnd(result=Clear)
- 게임 종료 시: DB 저장 -> 플레이어 로비 복귀

---

## 데이터베이스 스키마 변경

### 추가 테이블 (schema_game.sql)
```sql
CREATE TABLE characters (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    account_id  BIGINT NOT NULL UNIQUE,
    level       INT NOT NULL DEFAULT 1,
    exp         BIGINT NOT NULL DEFAULT 0,
    hp          INT NOT NULL DEFAULT 100,
    max_hp      INT NOT NULL DEFAULT 100,
    attack      INT NOT NULL DEFAULT 10,
    defense     INT NOT NULL DEFAULT 5,
    x           FLOAT NOT NULL DEFAULT 400.0,
    y           FLOAT NOT NULL DEFAULT 300.0,
    updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id)
);
```

---

## Protocol Buffers 설계

### room.proto 추가 메시지
```proto
message ReqReadyGame { }
message NotiGameStart { int32 zone_id = 1; }
message NotiGameEnd { bool is_clear = 1; int32 exp_gained = 2; }
```

### world.proto
```proto
message ReqMove { float x = 1; float y = 2; }
message NotiMove { uint64 player_id = 1; float x = 2; float y = 3; }
message NotiEnterGame {
    uint64 player_id = 1; string player_name = 2;
    float x = 3; float y = 4; int32 level = 5;
    int32 hp = 6; int32 max_hp = 7;
}
message NotiLeaveGame { uint64 player_id = 1; }
message NotiSpawnMonster { uint64 monster_id = 1; int32 monster_type = 2; float x = 3; float y = 4; int32 hp = 5; int32 max_hp = 6; }
message NotiDespawnMonster { uint64 monster_id = 1; }
message ResEnterGame {
    repeated PlayerInfo players = 1;
    repeated MonsterInfo monsters = 2;
    ErrorCode error_code = 3;
}
message PlayerInfo { uint64 player_id = 1; string name = 2; float x = 3; float y = 4; int32 level = 5; int32 hp = 6; int32 max_hp = 7; }
message MonsterInfo { uint64 monster_id = 1; int32 monster_type = 2; float x = 3; float y = 4; int32 hp = 5; int32 max_hp = 6; }
```

### combat.proto (PvE only)
```proto
message ReqAttack { uint64 target_monster_id = 1; }
message NotiCombat { uint64 attacker_player_id = 1; uint64 target_monster_id = 2; int32 damage = 3; }
message NotiMonsterAttack { uint64 monster_id = 1; uint64 target_player_id = 2; int32 damage = 3; }
message NotiHpChange { uint64 entity_id = 1; int32 hp = 2; int32 max_hp = 3; bool is_monster = 4; }
message NotiDeath { uint64 entity_id = 1; bool is_monster = 2; }
message NotiRespawn { uint64 monster_id = 1; float x = 2; float y = 3; int32 hp = 4; }
```

### character.proto
```proto
message NotiExpGain { uint64 player_id = 1; int32 exp_gained = 2; int64 total_exp = 3; int64 next_level_exp = 4; }
message NotiLevelUp { uint64 player_id = 1; int32 new_level = 2; int32 new_max_hp = 3; int32 new_attack = 4; int32 new_defense = 5; }
message ResCharacterInfo { int32 level = 1; int64 exp = 2; int64 next_level_exp = 3; int32 hp = 4; int32 max_hp = 5; int32 attack = 6; int32 defense = 7; float x = 8; float y = 9; }
```

### chat.proto
```proto
message ReqChat { string message = 1; }
message NotiChat { uint64 player_id = 1; string player_name = 2; string message = 3; }
```

---

## 레벨업 테이블 (간단)
```
Level 1 -> 2: 100 EXP
Level N -> N+1: 100 * N * 1.2^(N-1) EXP
```

## 몬스터 스탯 테이블
| Type | Name | HP | ATK | DEF | EXP | 리스폰(초) |
|------|------|----|-----|-----|-----|-----------|
| 1 | Slime | 30 | 3 | 0 | 10 | 5 |
| 2 | Orc | 80 | 8 | 3 | 30 | 10 |
| 3 | Dragon | 500 | 25 | 10 | 200 | 60 |

---

## 의존성 정보

- `DotNetty.Codecs.Http` 0.7.6 — WebSocket 지원 확인 필요 (패키지에 포함되어 있음)
- `protobuf.js` v7.x — 클라이언트 Protobuf 직렬화
- HTML5 Canvas API — 렌더링
