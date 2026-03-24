# RPG 웹게임 서버 구현 계획

Last Updated: 2026-03-24

---

## Executive Summary

기존 DhNet_DotNetty 인프라(DotNetty + Protocol Buffers + AES-GCM + MySQL)를 재활용하여
브라우저에서 플레이 가능한 간단한 2D 멀티플레이어 PvE RPG를 구현한다.

**핵심 결정:**
- 로비/룸 시스템 **유지** — 사용자가 룸을 직접 생성하고 입장하는 방식
- 멀티플레이어: **PvE 전용** (플레이어 간 전투 없음)
- 룸당 최대 **2인**
- DotNetty에 **WebSocket 지원 추가** (브라우저 연결)
- Protocol Buffers를 WebSocket 바이너리 프레임으로 전송
- HTML5 Canvas 기반 경량 웹 클라이언트 신규 작성
- RPG 시스템(GameSession, Monster, Combat, Character Stats) 신규 구현

---

## Current State Analysis

### 재활용 가능 컴포넌트
| 컴포넌트 | 상태 | 비고 |
|---------|------|------|
| DotNetty Pipeline (Framing + Protobuf + Crypto) | 재활용 | WebSocket 레이어 추가 |
| SessionComponent | 재활용 | 수정 없음 |
| PlayerComponent | 재활용 (수정) | GameSession 서브컴포넌트 추가 |
| LobbyComponent | 재활용 | 룸 목록 표시 로직 유지 |
| RoomComponent | 재활용 (수정) | 최대 인원 2로 제한, 게임 시작 트리거 추가 |
| PlayerLobbyComponent | 재활용 | 수정 최소화 |
| PlayerRoomComponent | 재활용 (수정) | 게임 중 상태 연동 |
| LobbySystem | 재활용 | 수정 없음 |
| PlayerSystem + WorkerSystem | 재활용 | 변경 없음 |
| LoginProcessor / RegisterProcessor | 재활용 | CharacterComponent 초기화 연동 추가 |
| HeartbeatHandler | 재활용 | 변경 없음 |
| DatabaseSystem + Dapper | 재활용 | 테이블 추가 |
| Web REST API | 재활용 | 엔드포인트 조정 |
| AES-GCM 암호화 | 재활용 | 변경 없음 |

### 수정 대상
| 수정 항목 | 변경 내용 |
|-----------|----------|
| RoomComponent | 최대 인원 2로 제한, 게임 시작 콜백 추가 |
| PlayerRoomComponent | 게임 플레이 중 상태(위치, HP) 연동 |
| lobby.proto | 변경 없음 또는 최소 조정 |
| room.proto | ReqReadyGame, NotiGameStart 패킷 추가 |
| PlayerLobbyController | 변경 없음 |
| PlayerRoomController | ReqReadyGame 핸들러 추가 |

---

## Proposed Future State

### 아키텍처 개요

```
[Browser HTML5 Client]
       |  WebSocket (ws://host:7777/ws)
       |  Binary frames: LengthField + ProtobufMessage
       v
[DotNetty Pipeline]
  HttpServerCodec
  HttpObjectAggregator
  WebSocketServerProtocolHandler  <- NEW
  LengthFieldPrepender/Decoder
  ProtobufDecoder/Encoder (기존 유지)
  AesGcmDecryption/Encryption (선택)
  HeartbeatHandler
  GameServerHandler  <- 패킷 라우팅 수정
       |
       v
[PlayerComponent] -> WorkerSystem (100ms tick)
  PlayerLobbyComponent  <- 기존 유지
  PlayerRoomComponent   <- 기존 유지 (게임 상태 연동)
  PlayerWorldComponent  <- NEW (위치, HP, 전투상태)
  CharacterComponent    <- NEW (레벨, 경험치, 스탯)
       |
       +-- LobbySystem  <- 기존 유지
       |     +-- LobbyComponent (룸 목록)
       |
       +-- RoomComponent (max 2인)  <- 수정
       |     +-- GameSessionComponent  <- NEW
       |           +-- MonsterComponent[] (PvE 몬스터)
       |
       +-- MonsterSystem  <- NEW (몬스터 AI, 리스폰)
       +-- CombatSystem   <- NEW (PvE 데미지 계산, EXP 분배)
```

### 게임 루프 설계
```
로그인
  -> 로비 입장 (NotiEnterLobby, 룸 목록 수신)
  -> 룸 생성(ReqCreateRoom) 또는 룸 입장(ReqEnterRoom)
  -> 룸 대기 (최대 2인, Ready 상태 관리)
  -> 게임 시작 (ReqReadyGame -> 2인 준비 완료 or 방장 강제 시작)
  -> GameSession 시작 (NotiGameStart)
       |
       +- 이동      (ReqMove    -> NotiMove 브로드캐스트)
       +- 공격      (ReqAttack  -> 서버 판정 -> NotiCombat -> NotiDamage)  [PvE only]
       +- 채팅      (ReqChat    -> NotiChat 브로드캐스트)
       +- 레벨업    (NotiLevelUp -> stats 업데이트)
       +- 게임 종료 (모든 웨이브 클리어 or 전원 사망 -> NotiGameEnd)
  -> 결과 화면 -> 로비 복귀
```

### RPG 게임 스펙
- **맵**: 룸당 단일 게임 맵 (800x600 타일, 각 타일 32px)
- **플레이어**: 룸당 최대 2인
- **몬스터**: Slime, Orc, Dragon (3종, 고정 리스폰 포인트)
- **전투**: 클릭 -> 공격 (PvE only, 쿨다운 1초) — 플레이어 간 전투 없음
- **스탯**: HP, MaxHP, ATK, DEF, Level, EXP, NextLevelEXP
- **레벨**: 1~50, EXP 테이블 고정
- **아이템**: 드롭 -> 자동 줍기 (단순화)
- **게임 종료**: 전원 사망 or 보스(Dragon) 처치

---

## Implementation Phases

### Phase 1: 네트워크 레이어 WebSocket 전환 (M)
**목표**: 브라우저에서 DotNetty 서버에 WebSocket으로 연결

1.1 `GamePipelineInitializer`에 WebSocket 핸드셰이크 핸들러 추가
1.2 WebSocket 바이너리 프레임 <-> ByteBuf 변환 핸들러 작성
1.3 GameClient 프로젝트에 WebSocket 지원 추가 (기존 부하 테스트 유지)
1.4 간단한 HTML 테스트 페이지로 WebSocket 연결 검증

**수용 기준**: 브라우저 JS에서 ws://localhost:7777/ws 연결 후 ReqRegister/ReqLogin 성공

---

### Phase 2: 룸 시스템 수정 및 Proto 확장 (M)
**목표**: 기존 로비/룸 유지 + 게임 시작 흐름 추가, RPG용 proto 정의

2.1 `RoomComponent` 수정 — 최대 인원 2로 제한, 게임 시작 콜백 추가
2.2 `room.proto` 확장 — `ReqReadyGame`, `NotiGameStart`, `NotiGameEnd` 추가
2.3 `PlayerRoomController` 수정 — `ReqReadyGame` 핸들러 추가
2.4 신규 proto 파일 작성:
  - `world.proto` — 이동, 게임 내 입/퇴장, 스폰/디스폰
  - `combat.proto` — PvE 공격, 데미지, 사망, 리스폰
  - `character.proto` — 캐릭터 정보, 레벨업, 경험치
  - `chat.proto` — 단순 채팅 (룸/게임 내)
2.5 `game_packet.proto` oneof에 신규 패킷 추가

**수용 기준**: 빌드 성공, 기존 로그인/등록/로비/룸 입장 동작 유지, 룸 최대 2인 제한 동작

---

### Phase 3: RPG 서버 컴포넌트 구현 (L)
**목표**: RPG 핵심 서버 로직

3.1 **CharacterComponent** — 레벨, EXP, HP, ATK, DEF 관리
3.2 **PlayerWorldComponent** — 위치(x,y), 이동 처리, 전투 상태
3.3 **GameSessionComponent** — 룸과 1:1 연결, 몬스터 관리, 브로드캐스트 (max 2인)
3.4 **MonsterComponent** — 위치, HP, 타입, 공격 AI, 리스폰 타이머
3.5 **MonsterSystem** — 몬스터 생성/삭제/리스폰, 틱 업데이트
3.6 **CombatSystem** — PvE 데미지 공식, EXP 분배, 레벨업 처리
3.7 **PlayerRpgController** — ReqMove, ReqAttack(PvE only), ReqChat 핸들링
3.8 **LoginProcessor 수정** — 로그인 시 CharacterComponent 초기화 연동

**수용 기준**: 2인 접속 -> 룸 생성/입장 -> 게임 시작 -> 이동/전투/채팅/레벨업 동작 확인

---

### Phase 4: 데이터베이스 레이어 확장 (S)
**목표**: RPG 데이터 영속화

4.1 DB 스키마 추가:
  ```sql
  characters (account_id, hp, max_hp, attack, defense, level, exp, x, y, updated_at)
  ```
4.2 **CharacterRow** 클래스 작성
4.3 **CharacterDbSet** — INSERT (최초 생성) / UPDATE (로그아웃 저장) / SELECT (로그인 로드)
4.4 **DatabaseSystem**에 CharacterDbSet 등록

**수용 기준**: 로그인 시 캐릭터 데이터 로드, 로그아웃/게임 종료 시 레벨/HP 저장

---

### Phase 5: HTML5 웹 클라이언트 (L)
**목표**: 브라우저에서 플레이 가능한 최소 클라이언트

5.1 **index.html** — 로그인/로비/룸/게임 화면 레이아웃
5.2 **game.js** — WebSocket 연결, protobuf.js 처리, 게임 루프
5.3 **renderer.js** — Canvas 타일맵, 스프라이트 렌더링, HP바
5.4 **ui.js** — 로그인, 로비 룸 목록, 룸 대기실, 채팅창, 스탯 창

**클라이언트 화면 플로우:**
```
[로그인 화면]
  -> [로비 화면] (룸 목록, 룸 생성 버튼, 입장 버튼)
  -> [룸 대기실] (최대 2인, Ready 버튼, 채팅)
  -> [게임 화면] (Canvas 맵, 플레이어/몬스터 렌더링, HP바, 스탯)
  -> [결과 화면] (클리어/전멸, 경험치/레벨 결과)
  -> 로비 복귀
```

**클라이언트 구조:**
```
GameClient.Web/ (new project)
+-- wwwroot/
|   +-- index.html
|   +-- js/
|   |   +-- game.js       # WebSocket + 게임 루프
|   |   +-- renderer.js   # Canvas 렌더링
|   |   +-- ui.js         # UI 관리 (로비, 룸, 게임)
|   |   +-- proto/        # protobuf.js + 생성된 .js 파일
|   +-- css/
|       +-- style.css
+-- GameClient.Web.csproj  # ASP.NET Core 정적 파일 서빙
```

**수용 기준**: Chrome/Firefox에서 접속 -> 로그인 -> 로비 -> 룸 생성/입장 -> 게임 플레이 -> 결과

---

### Phase 6: 통합 테스트 및 부하 테스트 업데이트 (S)
**목표**: 기존 TestClient 부하 테스트를 RPG 시나리오로 업데이트

6.1 `RpgRoomScenario` — Register -> Login -> CreateRoom -> ReadyGame -> Move -> Attack
6.2 `PveStressScenario` — 2인 쌍 다수 동시 룸 테스트
6.3 Web REST API 엔드포인트 조정 (Rooms 조회 등 유지)

---

## Detailed Tasks

### Phase 1 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 1.1 | WebSocketFrameHandler 작성 | BinaryWebSocketFrame -> ByteBuf 변환 | S | - |
| 1.2 | GamePipelineInitializer WebSocket 분기 추가 | /ws 경로로 WebSocket 업그레이드 | S | 1.1 |
| 1.3 | WebSocket 연결 테스트 HTML 페이지 | 브라우저에서 연결 확인 | S | 1.2 |

### Phase 2 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 2.1 | RoomComponent 최대 인원 2 제한 | 3번째 입장 시 거절 | XS | - |
| 2.2 | room.proto — ReqReadyGame, NotiGameStart, NotiGameEnd 추가 | 컴파일 성공 | S | - |
| 2.3 | PlayerRoomController — ReqReadyGame 핸들러 | 2인 준비 시 NotiGameStart 발송 | S | 2.1, 2.2 |
| 2.4 | world.proto 작성 | 이동/게임세션 패킷 정의 | S | - |
| 2.5 | combat.proto 작성 (PvE only) | 전투/데미지/사망 패킷 정의 | S | - |
| 2.6 | character.proto 작성 | 캐릭터 정보/레벨업 패킷 정의 | S | - |
| 2.7 | chat.proto 작성 | 채팅 패킷 정의 | XS | - |
| 2.8 | game_packet.proto 업데이트 | oneof에 신규 패킷 추가 | S | 2.4~2.7 |

### Phase 3 Tasks

| # | 작업 | 수용 기준 | 크기 | 의존성 |
|---|------|-----------|------|--------|
| 3.1 | CharacterComponent 구현 | 스탯 관리, 레벨업 로직 | M | Phase 2 |
| 3.2 | PlayerWorldComponent 구현 | 위치/이동/전투상태 관리 | M | 3.1 |
| 3.3 | GameSessionComponent 구현 | 룸 연동, 몬스터 관리, 브로드캐스트 (max 2인) | M | - |
| 3.4 | MonsterComponent 구현 | AI, 리스폰, 전투 | M | 3.3 |
| 3.5 | MonsterSystem 구현 | 틱 기반 몬스터 관리 | M | 3.4 |
| 3.6 | CombatSystem 구현 (PvE) | PvE 데미지 계산, EXP 분배 | M | 3.1, 3.4 |
| 3.7 | PlayerRpgController 구현 | ReqMove, ReqAttack(PvE), ReqChat | M | 3.1~3.6 |
| 3.8 | LoginProcessor RPG 연동 | 로그인 시 CharacterComponent 초기화 | S | 3.1, Phase 4 |

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
| 5.3 | 로그인 UI + WebSocket 연결 | 로그인 성공 후 로비 화면 전환 | M | Phase 1 |
| 5.4 | 로비 UI — 룸 목록, 생성, 입장 | 룸 생성/입장 동작 | M | 5.3 |
| 5.5 | 룸 대기실 UI — 인원 표시, Ready 버튼 | 2인 Ready 후 게임 시작 | S | 5.4 |
| 5.6 | 타일맵 렌더러 | 캔버스에 맵 타일 표시 | M | 5.5 |
| 5.7 | 플레이어/몬스터 렌더링 | 스프라이트 + 이름 + HP바 | M | 5.6 |
| 5.8 | 이동 입력 처리 | WASD/화살표키 -> ReqMove -> NotiMove 반영 | M | 5.7 |
| 5.9 | 전투 UI (PvE) | 클릭 공격, 데미지 수치 표시, 몬스터 사망 처리 | M | 5.8 |
| 5.10 | 채팅창 UI | 채팅 입력/표시 | S | 5.6 |
| 5.11 | 스탯/레벨 UI | HP바, 경험치바, 레벨 표시 | S | 5.6 |
| 5.12 | 결과 화면 | 클리어/전멸, 레벨/EXP 결과 표시 후 로비 복귀 | S | 5.9 |

---

## Risk Assessment

| 위험 | 가능성 | 영향 | 완화 전략 |
|------|--------|------|-----------|
| DotNetty WebSocket + Protobuf 프레이밍 충돌 | 중 | 높 | WebSocket 프레임을 Protobuf 앞단에 배치, 바이너리 프레임 분리 처리 |
| 브라우저 protobuf.js 호환성 | 낮 | 중 | protobuf.js 라이브러리 테스트 먼저 진행 |
| MonsterSystem 틱 성능 (다수 룸) | 중 | 중 | 룸당 몬스터 30개 제한, GameSessionComponent 내 WorkerSystem 재활용 |
| 멀티플레이어 이동 동기화 지연 | 중 | 중 | 서버 권위 모델, 클라이언트 보간으로 부드러운 움직임 |
| 캐릭터 DB 저장 타이밍 (로그아웃 누락) | 중 | 중 | 연결 끊김 시 ShutdownSystem과 연동하여 강제 저장 |

---

## Success Metrics

1. **기능**: 2인 동시 접속 -> 룸 생성/입장 -> 이동/PvE 전투/채팅 정상 동작
2. **성능**: 10개 룸(20명) 동시 운영 시 틱 지연 < 200ms
3. **안정성**: 30분 연속 플레이 시 크래시 없음
4. **데이터**: 로그인/로그아웃 반복 후 캐릭터 데이터(레벨, HP) 정확히 저장

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
| Phase 2: 룸 수정 + Proto 확장 | M | 1 (블로커) |
| Phase 3: RPG 서버 컴포넌트 | L | 2 |
| Phase 4: DB 확장 | S | 2 |
| Phase 5: HTML5 클라이언트 | L | 3 |
| Phase 6: 테스트 업데이트 | S | 4 |
