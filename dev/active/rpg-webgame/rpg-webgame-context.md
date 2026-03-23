# RPG 웹게임 - Context

Last Updated: 2026-03-23

---

## 요구사항 요약

- 기존 DotNetty + Protocol Buffers 유지
- 로비/룸 시스템 제거
- 브라우저에서 플레이 가능한 간단한 웹 RPG
- 멀티플레이어 (복수 플레이어 동시 접속)

---

## 핵심 파일 목록

### 재활용 (수정 없거나 최소 수정)
- `GameServer/Network/GameServerBootstrap.cs` - DotNetty 서버 부트스트랩
- `GameServer/Network/GamePipelineInitializer.cs` - **수정 필요**: WebSocket 핸들러 추가
- `GameServer/Network/SessionComponent.cs` - 세션 상태
- `GameServer/Network/GameServerHandler.cs` - **수정 필요**: RPG 패킷 라우팅 추가
- `GameServer/Network/LoginProcessor.cs` - **수정 필요**: CharacterComponent 초기화 연동
- `GameServer/Network/RegisterProcessor.cs` - 재활용
- `GameServer/Network/HeartbeatHandler.cs` - 재활용
- `GameServer/Systems/PlayerSystem.cs` - 재활용
- `GameServer/Systems/ShutdownSystem.cs` - 재활용
- `GameServer/Systems/GameSystems.cs` - **수정 필요**: ZoneSystem, MonsterSystem 추가
- `Common.Server/Component/WorkerSystem.cs` - 재활용
- `Common.Server/Routing/PacketRouter.cs` - 재활용
- `GameServer.Database/` - 전체 재활용 + CharacterDbSet 추가

### 삭제 대상
- `GameServer/Component/Lobby/LobbyComponent.cs`
- `GameServer/Component/Room/RoomComponent.cs`
- `GameServer/Component/Player/PlayerLobbyComponent.cs`
- `GameServer/Component/Player/PlayerRoomComponent.cs`
- `GameServer/Controllers/PlayerLobbyController.cs`
- `GameServer/Controllers/PlayerRoomController.cs`
- `GameServer/Systems/LobbySystem.cs`
- `GameServer.Protocol/Protos/lobby.proto`
- `GameServer.Protocol/Protos/room.proto`

### 신규 작성
- `GameServer/Network/WebSocketFrameHandler.cs` - WebSocket 바이너리 프레임 처리
- `GameServer/Component/Player/PlayerWorldComponent.cs` - 위치/이동/전투상태
- `GameServer/Component/Player/CharacterComponent.cs` - 스탯/레벨/EXP
- `GameServer/Component/Zone/ZoneComponent.cs` - 존 (플레이어+몬스터 관리)
- `GameServer/Component/Monster/MonsterComponent.cs` - 몬스터 엔티티
- `GameServer/Systems/ZoneSystem.cs` - 존 레지스트리
- `GameServer/Systems/MonsterSystem.cs` - 몬스터 AI + 리스폰
- `GameServer/Systems/CombatSystem.cs` - 전투 계산
- `GameServer/Controllers/PlayerRpgController.cs` - RPG 패킷 핸들러
- `GameServer.Protocol/Protos/world.proto` - 이동/존 패킷
- `GameServer.Protocol/Protos/combat.proto` - 전투 패킷
- `GameServer.Protocol/Protos/character.proto` - 캐릭터 정보 패킷
- `GameServer.Protocol/Protos/chat.proto` - 채팅 패킷
- `GameServer.Database/DbSet/CharacterDbSet.cs` - 캐릭터 DB 접근
- `GameServer.Database/Rows/CharacterRow.cs` - 캐릭터 DB 행
- `GameClient.Web/` - HTML5 웹 클라이언트 프로젝트 (신규)

---

## 핵심 설계 결정

### 1. WebSocket + Protobuf 프레이밍
DotNetty WebSocket 지원은 `DotNetty.Codecs.Http` 패키지에 포함됨.
파이프라인 구성:
```
HttpServerCodec
HttpObjectAggregator
WebSocketServerProtocolHandler (path: /ws)
WebSocketFrameHandler (BinaryWebSocketFrame → ByteBuf)
LengthFieldPrepender(2) / LengthFieldBasedFrameDecoder
ProtobufDecoder / ProtobufEncoder
AesGcmDecryption/Encryption (optional)
HeartbeatHandler
GameServerHandler
```

### 2. 서버 권위 이동
- 클라이언트가 ReqMove(targetX, targetY) 전송
- 서버가 유효성 검사 (이동 속도 초과 여부) 후 위치 업데이트
- NotiMove를 존의 모든 플레이어에게 브로드캐스트
- 클라이언트는 보간(interpolation)으로 부드러운 움직임 표현

### 3. 전투 모델
- 클릭 → ReqAttack(targetMonsterId 또는 targetPlayerId)
- 서버: 사거리 체크 → 데미지 계산 (ATK - DEF, 최소 1) → NotiCombat 브로드캐스트
- 몬스터 HP 0 → NotiDeath → EXP 분배 → NotiExpGain → 레벨업 체크
- 몬스터 리스폰: 사망 후 10초 타이머

### 4. 캐릭터 데이터 저장 타이밍
- 로그인: DB에서 characters 레코드 로드 (없으면 기본값으로 생성)
- 로그아웃/연결 끊김: characters 레코드 업데이트 (HP, 위치, 레벨, EXP)
- 레벨업: 즉시 DB 업데이트 (데이터 손실 방지)

### 5. 존 설계
- 초기에는 단일 존 (ZoneId = 1)으로 단순화
- 향후 다중 존 확장 가능 (ZoneSystem에 추상화)
- 존 내 몬스터: 사전 정의된 스폰 포인트 (JSON 또는 코드 하드코딩)

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
    zone_id     INT NOT NULL DEFAULT 1,
    updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id)
);
```

---

## Protocol Buffers 설계

### world.proto
```proto
message ReqMove { float x = 1; float y = 2; }
message NotiMove { uint64 player_id = 1; float x = 2; float y = 3; }
message NotiEnterZone {
    uint64 player_id = 1; string player_name = 2;
    float x = 3; float y = 4; int32 level = 5;
    int32 hp = 6; int32 max_hp = 7;
}
message NotiLeaveZone { uint64 player_id = 1; }
message NotiSpawnMonster { uint64 monster_id = 1; int32 monster_type = 2; float x = 3; float y = 4; int32 hp = 5; int32 max_hp = 6; }
message NotiDespawnMonster { uint64 monster_id = 1; }
message ResEnterZone {
    repeated PlayerInfo players = 1;
    repeated MonsterInfo monsters = 2;
    ErrorCode error_code = 3;
}
message PlayerInfo { uint64 player_id = 1; string name = 2; float x = 3; float y = 4; int32 level = 5; int32 hp = 6; int32 max_hp = 7; }
message MonsterInfo { uint64 monster_id = 1; int32 monster_type = 2; float x = 3; float y = 4; int32 hp = 5; int32 max_hp = 6; }
```

### combat.proto
```proto
message ReqAttack { uint64 target_id = 1; bool is_monster = 2; }
message NotiCombat { uint64 attacker_id = 1; uint64 target_id = 2; int32 damage = 3; bool is_monster_target = 4; }
message NotiHpChange { uint64 entity_id = 1; int32 hp = 2; int32 max_hp = 3; bool is_monster = 4; }
message NotiDeath { uint64 entity_id = 1; bool is_monster = 2; }
message NotiRespawn { uint64 entity_id = 1; float x = 2; float y = 3; int32 hp = 4; bool is_monster = 5; }
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
Level 1 → 2: 100 EXP
Level N → N+1: 100 * N * 1.2^(N-1) EXP
```

## 몬스터 스탯 테이블
| Type | Name | HP | ATK | DEF | EXP | 리스폰(초) |
|------|------|----|-----|-----|-----|-----------|
| 1 | Slime | 30 | 3 | 0 | 10 | 5 |
| 2 | Orc | 80 | 8 | 3 | 30 | 10 |
| 3 | Dragon | 500 | 25 | 10 | 200 | 60 |

---

## 의존성 정보

- `DotNetty.Codecs.Http` 0.7.6 - WebSocket 지원 확인 필요 (패키지에 포함되어 있음)
- `protobuf.js` v7.x - 클라이언트 Protobuf 직렬화
- HTML5 Canvas API - 렌더링
