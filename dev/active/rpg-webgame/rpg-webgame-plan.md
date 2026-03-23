# RPG 웹게임 서버 구현 계획

Last Updated: 2026-03-23

---

## Executive Summary

기존 DhNet_DotNetty 인프라(DotNetty + Protocol Buffers + AES-GCM + MySQL)를 재활용하여
브라우저에서 플레이 가능한 간단한 2D 멀티플레이어 RPG를 구현한다.

**핵심 결정:**
- 로비/룸 시스템 **완전 제거**
- DotNetty에 **WebSocket 지원 추가** (브라우저 연결)
- Protocol Buffers를 WebSocket 바이너리 프레임으로 전송
- HTML5 Canvas 기반 경량 웹 클라이언트 신규 작성
- RPG 시스템(Zone, Monster, Combat, Character Stats) 신규 구현

---

## Current State Analysis

### 재활용 가능 컴포넌트
| 컴포넌트 | 상태 | 비고 |
|---------|------|------|
| DotNetty Pipeline (Framing + Protobuf + Crypto) | ✅ 재활용 | WebSocket 레이어 추가 |
| SessionComponent | ✅ 재활용 | 거의 수정 없음 |
| PlayerComponent | ✅ 재활용 (수정) | Lobby/Room 서브컴포넌트 제거 |
| PlayerSystem + WorkerSystem | ✅ 재활용 | 변경 없음 |
| LoginProcessor / RegisterProcessor | ✅ 재활용 | 거의 수정 없음 |
| HeartbeatHandler | ✅ 재활용 | 변경 없음 |
| DatabaseSystem + Dapper | ✅ 재활용 | 테이블 추가 |
| Web REST API | ✅ 재활용 | 엔드포인트 조정 |
| AES-GCM 암호화 | ✅ 재활용 | 변경 없음 |

### 제거 대상
| 제거 항목 | 이유 |
|-----------|------|
| LobbyComponent, LobbySystem | RPG에 불필요 |
| RoomComponent | RPG에 불필요 |
| PlayerLobbyComponent | 제거 |
| PlayerRoomComponent | 제거 |
| lobby.proto, room.proto | RPG proto로 교체 |
| PlayerLobbyController, PlayerRoomController | RPG 컨트롤러로 교체 |

---

## Proposed Future State

### 아키텍처 개요

```
[Browser HTML5 Client]
       │  WebSocket (ws://host:7777/ws)
       │  Binary frames: LengthField + ProtobufMessage
       ▼
[DotNetty Pipeline]
  HttpServerCodec
  HttpObjectAggregator
  WebSocketServerProtocolHandler ← NEW
  LengthFieldPrepender/Decoder
  ProtobufDecoder/Encoder (기존 유지)
  AesGcmDecryption/Encryption (선택)
  HeartbeatHandler
  GameServerHandler ← 패킷 라우팅 수정
       │
       ▼
[PlayerComponent] → WorkerSystem (100ms tick)
  PlayerWorldComponent  ← NEW (위치, HP, 전투상태)
  CharacterComponent    ← NEW (레벨, 경험치, 스탯)
       │
       ├── ZoneSystem (← LobbySystem 대체)
       │     └── ZoneComponent (← LobbyComponent 대체)
       │           ├── ConcurrentDictionary<ulong, PlayerComponent>
       │           └── ConcurrentDictionary<ulong, MonsterComponent>
       │
       ├── MonsterSystem ← NEW
       │     └── 몬스터 AI, 리스폰 관리
       │
       └── CombatSystem ← NEW
             └── 데미지 계산, 경험치 분배
```

### 게임 루프 설계
```
로그인 → 캐릭터 로드/생성 → 존 입장 → 게임 플레이 → 로그아웃
         (DB에서 stats 조회)  (NotiEnterZone 브로드캐스트)
                                    │
                                    ├─ 이동 (ReqMove → NotiMove 브로드캐스트)
                                    ├─ 공격 (ReqAttack → NotiCombat → NotiDamage)
                                    ├─ 채팅 (ReqChat → NotiChat 브로드캐스트)
                                    └─ 레벨업 (NotiLevelUp → stats 업데이트)
```

### RPG 게임 스펙 (단순화)
- **맵**: 단일 존 (800x600 타일, 각 타일 32px)
- **플레이어**: 최대 100명 동시
- **몬스터**: Slime, Orc, Dragon (3종, 고정 리스폰 포인트)
- **전투**: 클릭 → 공격 (실시간, 쿨다운 1초)
- **스탯**: HP, MaxHP, ATK, DEF, Level, EXP, NextLevelEXP
- **레벨**: 1~50, EXP 테이블 고정
- **아이템**: 드롭 → 자동 줍기 (단순화)

---

## Implementation Phases

### Phase 1: 네트워크 레이어 WebSocket 전환 (M)
**목표**: 브라우저에서 DotNetty 서버에 WebSocket으로 연결

1.1 `GamePipelineInitializer`에 WebSocket 핸드셰이크 핸들러 추가
1.2 WebSocket 바이너리 프레임 ↔ ByteBuf 변환 핸들러 작성
1.3 GameClient 프로젝트에 WebSocket 지원 추가 (기존 부하 테스트 유지)
1.4 간단한 HTML 테스트 페이지로 WebSocket 연결 검증

**수용 기준**: 브라우저 JS에서 ws://localhost:7777/ws 연결 후 ReqRegister/ReqLogin 성공

---

### Phase 2: 기존 Lobby/Room 제거 및 Proto 재정의 (M)
**목표**: 불필요한 코드 제거, RPG용 proto 정의

2.1 lobby.proto, room.proto → **삭제**
2.2 game_packet.proto oneof에서 lobby/room 패킷 **제거**
2.3 신규 proto 파일 작성:
  - `world.proto` - 이동, 존 입/퇴장, 스폰/디스폰
  - `combat.proto` - 공격, 데미지, 사망, 리스폰
  - `character.proto` - 캐릭터 정보, 레벨업, 경험치
  - `chat.proto` - 단순 채팅 (전역/존)
2.4 LobbyComponent, RoomComponent, 관련 Controller **삭제**
2.5 PlayerComponent에서 LobbyComponent, RoomComponent 참조 **제거**

**수용 기준**: 빌드 성공, 기존 로그인/등록/하트비트 동작 유지

---

### Phase 3: RPG 서버 컴포넌트 구현 (L)
**목표**: RPG 핵심 서버 로직

3.1 **CharacterComponent** - 레벨, EXP, HP, ATK, DEF 관리
3.2 **PlayerWorldComponent** - 위치(x,y,zoneId), 이동 처리, 전투 상태
3.3 **ZoneComponent** - 플레이어/몬스터 관리, 브로드캐스트
3.4 **ZoneSystem** - ZoneComponent 싱글톤 레지스트리
3.5 **MonsterComponent** - 위치, HP, 타입, 공격 AI, 리스폰 타이머
3.6 **MonsterSystem** - 몬스터 생성/삭제/리스폰, 틱 업데이트
3.7 **CombatSystem** - 데미지 공식, EXP 분배, 레벨업 처리
3.8 **PlayerRpgController** - ReqMove, ReqAttack, ReqChat 핸들링

**수용 기준**: 복수 클라이언트 접속 후 이동/전투/채팅/레벨업 동작 확인

---

### Phase 4: 데이터베이스 레이어 확장 (S)
**목표**: RPG 데이터 영속화

4.1 DB 스키마 추가:
  ```sql
  characters (account_id, hp, max_hp, attack, defense, level, exp, x, y, zone_id)
  ```
4.2 **CharacterDbSet** - INSERT (최초 생성) / UPDATE (로그아웃 저장) / SELECT (로그인 로드)
4.3 기존 `room_logs` 테이블 → **제거** (또는 유지)
4.4 **combat_logs** 선택적 추가 (킬/데스 기록)

**수용 기준**: 로그인 시 캐릭터 데이터 로드, 로그아웃 시 위치/HP/레벨 저장

---

### Phase 5: HTML5 웹 클라이언트 (L)
**목표**: 브라우저에서 플레이 가능한 최소 클라이언트

5.1 **index.html** - 게임 캔버스 + UI 레이아웃
5.2 **game.js** - WebSocket 연결, protobuf.js 처리, 게임 루프
5.3 **renderer.js** - Canvas 타일맵, 스프라이트 렌더링, HP바
5.4 **ui.js** - 로그인 화면, 채팅창, 스탯 창

**클라이언트 구조:**
```
GameClient.Web/ (new project)
├── wwwroot/
│   ├── index.html
│   ├── js/
│   │   ├── game.js       # WebSocket + 게임 루프
│   │   ├── renderer.js   # Canvas 렌더링
│   │   ├── ui.js         # UI 관리
│   │   └── proto/        # protobuf.js + 생성된 .js 파일
│   └── css/
│       └── style.css
└── GameClient.Web.csproj  # ASP.NET Core 정적 파일 서빙
```

**수용 기준**: Chrome/Firefox에서 접속 → 로그인 → 맵에 플레이어 표시 → 이동/공격/채팅 동작

---

### Phase 6: 통합 테스트 및 부하 테스트 업데이트 (S)
**목표**: 기존 GameClient 부하 테스트 RPG 시나리오로 업데이트

6.1 `RpgScenario` - Register → Login → Move → Attack → Chat
6.2 `CombatStressScenario` - 다수 플레이어 동시 전투
6.3 Web REST API 엔드포인트 조정 (Rooms → Zones)

---

## Detailed Tasks

### Phase 1 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 1.1 | WebSocketFrameHandler 작성 | BinaryWebSocketFrame → ByteBuf 변환 | S | - |
| 1.2 | GamePipelineInitializer WebSocket 분기 추가 | /ws 경로로 WebSocket 업그레이드 | S | 1.1 |
| 1.3 | WebSocket 연결 테스트 HTML 페이지 | 브라우저에서 연결 확인 | S | 1.2 |

### Phase 2 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 2.1 | lobby.proto, room.proto 삭제 | - | XS | - |
| 2.2 | world.proto 작성 | 이동/존 패킷 정의 | S | - |
| 2.3 | combat.proto 작성 | 전투/데미지/사망 패킷 정의 | S | - |
| 2.4 | character.proto 작성 | 캐릭터 정보/레벨업 패킷 정의 | S | - |
| 2.5 | chat.proto 작성 | 채팅 패킷 정의 | XS | - |
| 2.6 | game_packet.proto 업데이트 | oneof에 신규 패킷 추가 | S | 2.2~2.5 |
| 2.7 | LobbyComponent/RoomComponent 삭제 | 빌드 성공 | S | - |
| 2.8 | PlayerComponent 정리 | 로비/룸 참조 제거 | S | 2.7 |

### Phase 3 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 3.1 | CharacterComponent 구현 | 스탯 관리, 레벨업 로직 | M | Phase 2 |
| 3.2 | PlayerWorldComponent 구현 | 위치/이동/전투상태 관리 | M | 3.1 |
| 3.3 | ZoneComponent 구현 | 플레이어/몬스터 등록, 브로드캐스트 | M | - |
| 3.4 | ZoneSystem 구현 | 존 싱글톤 레지스트리 | S | 3.3 |
| 3.5 | MonsterComponent 구현 | AI, 리스폰, 전투 | M | 3.3 |
| 3.6 | MonsterSystem 구현 | 틱 기반 몬스터 관리 | M | 3.5 |
| 3.7 | CombatSystem 구현 | 데미지 계산, EXP 분배 | M | 3.1, 3.5 |
| 3.8 | PlayerRpgController 구현 | ReqMove, ReqAttack, ReqChat | M | 3.1~3.7 |
| 3.9 | LoginProcessor RPG 연동 | 로그인 시 CharacterComponent 초기화 | S | 3.1, Phase 4 |

### Phase 4 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 4.1 | schema_game.sql에 characters 테이블 추가 | SQL 실행 성공 | XS | - |
| 4.2 | CharacterRow 클래스 작성 | DB row 매핑 | XS | 4.1 |
| 4.3 | CharacterDbSet 작성 | Insert/Update/Select | S | 4.2 |
| 4.4 | DatabaseSystem에 CharacterDbSet 등록 | 빌드+연결 성공 | XS | 4.3 |

### Phase 5 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 5.1 | GameClient.Web 프로젝트 생성 | ASP.NET Core 정적 파일 서빙 | S | - |
| 5.2 | protobuf.js 통합 + proto JS 생성 | 패킷 직렬화/역직렬화 동작 | M | Phase 2 |
| 5.3 | WebSocket 연결 + 로그인 UI | 로그인 성공 후 게임 화면 전환 | M | Phase 1 |
| 5.4 | 타일맵 렌더러 | 캔버스에 맵 타일 표시 | M | 5.3 |
| 5.5 | 플레이어/몬스터 렌더링 | 스프라이트 + 이름 + HP바 | M | 5.4 |
| 5.6 | 이동 입력 처리 | WASD/화살표키 → ReqMove → NotiMove 반영 | M | 5.5 |
| 5.7 | 전투 UI | 클릭 공격, 데미지 수치 표시, 사망 처리 | M | 5.6 |
| 5.8 | 채팅창 UI | 채팅 입력/표시 | S | 5.5 |
| 5.9 | 스탯/레벨 UI | HP바, 경험치바, 레벨 표시 | S | 5.5 |

---

## Risk Assessment

| 위험 | 가능성 | 영향 | 완화 전략 |
|------|--------|------|-----------|
| DotNetty WebSocket + Protobuf 프레이밍 충돌 | 중 | 높 | WebSocket 프레임을 Protobuf 앞단에 배치, 바이너리 프레임 분리 처리 |
| 브라우저 protobuf.js 호환성 | 낮 | 중 | protobuf.js 라이브러리 테스트 먼저 진행 |
| MonsterSystem 틱 성능 (다수 몬스터) | 중 | 중 | 몬스터 수 50개 제한, 존당 WorkerSystem 재활용 |
| 멀티플레이어 이동 동기화 지연 | 중 | 중 | 서버 권위 모델, 클라이언트 보간으로 부드러운 움직임 |
| 캐릭터 DB 저장 타이밍 (로그아웃 누락) | 중 | 중 | 연결 끊김 시 ShutdownSystem과 연동하여 강제 저장 |

---

## Success Metrics

1. **기능**: 2명 이상 동시 접속 → 이동/전투/채팅 정상 동작
2. **성능**: 50명 동시 접속 시 틱 지연 < 200ms
3. **안정성**: 30분 연속 플레이 시 크래시 없음
4. **데이터**: 로그인/로그아웃 반복 후 캐릭터 데이터(레벨, 위치) 정확히 저장

---

## Required Resources

- 기존 DhNet_DotNetty 솔루션 (모든 프로젝트 참조)
- DotNetty.Transport.Bootstrapping (기존)
- DotNetty.Codecs.Http (WebSocket 지원 - 버전 확인 필요)
- protobuf.js (npm/CDN - 클라이언트용)
- 간단한 RPG 스프라이트 (타일셋, 캐릭터, 몬스터)

---

## Timeline Estimates

| Phase | 크기 | 우선순위 |
|-------|------|---------|
| Phase 1: WebSocket 전환 | M | 1 (블로커) |
| Phase 2: Proto 재정의 + 코드 정리 | M | 1 (블로커) |
| Phase 3: RPG 서버 컴포넌트 | L | 2 |
| Phase 4: DB 확장 | S | 2 |
| Phase 5: HTML5 클라이언트 | L | 3 |
| Phase 6: 테스트 업데이트 | S | 4 |
