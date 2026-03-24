# RPG 웹게임 - Context

Last Updated: 2026-03-24 (Phase 6 완료)

---

## 현재 구현 상태

**Phase 1~6 전부 완료.** 미완료 항목: Phase 6.3 (Web REST API 확인) — 기능 코드 변경 없이 수동 검증만 필요.

### 완료된 Phase 요약
| Phase | 내용 | 상태 |
|-------|------|------|
| 1 | WebSocket 파이프라인 (`/ws`) | ✅ |
| 2 | 룸 시스템 수정 + Proto 확장 | ✅ |
| 3 | RPG 서버 컴포넌트 (Character, World, Monster, GameSession) | ✅ |
| 4 | DB 확장 (characters 테이블 + CharacterDbSet) | ✅ |
| 5 | HTML5 웹 클라이언트 (GameClient.Web) | ✅ |
| 6.1 | RpgRoomScenario (TestClient) | ✅ |
| 6.2 | PveStressScenario (TestClient) | ✅ |
| 6.3 | Web REST API 엔드포인트 확인 | ⬜ 수동 검증 |

---

## 이 세션에서 내린 주요 결정사항

### 1. protobuf.js oneof 접근 방식 (결정적 버그 수정)
- **문제**: 클라이언트 로그인/회원가입 후 "연결 중..."에서 멈춤
- **원인 1**: `pkt.payloadCase` 는 C# 관례. protobuf.js에서는 `pkt.payload` 가 설정된 필드 이름 문자열을 반환
  ```js
  // 틀림: pkt.payloadCase → undefined
  // 맞음: pkt.payload → "resLogin"
  switch(pkt.payload) { case 'resLogin': ... }
  ```
- **원인 2**: `uint64` 필드는 protobuf.js에서 `Long` 객체로 반환됨 (BigInt/number 아님)
  ```js
  function toId(v) {
    if (v == null) return '0';
    if (typeof v === 'object' && v.toString) return v.toString();
    return String(v);
  }
  ```
- **검증**: Node.js에서 직접 패킷 디코딩하여 확인
- game.js 전체 재작성으로 수정 완료 (2026-03-24)

### 2. WebSocket 파이프라인 — LengthField 프레이밍 불필요
- WebSocket이 메시지 경계를 자체 처리하므로 LengthFieldPrepender/BasedFrameDecoder 제거
- `WebSocketFrameHandler`: `BinaryWebSocketFrame` ↔ `IByteBuffer` 변환만 수행

### 3. GameClient.Web 프로젝트 구성
- `Microsoft.NET.Sdk` + `FrameworkReference Include="Microsoft.AspNetCore.App"` 사용
  (NOT `Microsoft.NET.Sdk.Web` — `WebApplication`을 찾지 못하는 이슈 있었음)
- `using Microsoft.AspNetCore.Builder;` 명시적 선언 필요 (ImplicitUsings로 포함 안 됨)

### 4. proto-bundle.json 생성 방법
- `npx pbjs` CLI는 잘못된 패키지(pbjs@0.0.14)를 설치함
- Node.js protobuf.js API를 직접 사용:
  ```js
  const protobuf = require('protobufjs');
  const root = new protobuf.Root();
  root.resolvePath = (o, t) => path.join('../GameServer.Protocol', t);
  root.loadSync('Protos/game_packet.proto', { keepCase: false });
  require('fs').writeFileSync('proto-bundle.json', JSON.stringify(root.toJSON()));
  ```

### 5. RpgRoomScenario / PveStressScenario 협조 패턴
- 짝수 인덱스 클라이언트: 룸 생성 → NotiRoomEnter(다른 플레이어) 수신 시 ReadyGame
- 홀수 인덱스 클라이언트: 2~3초 딜레이 → ReqRoomList → 입장 → 즉시 ReadyGame
- 1초 공격 쿨다운 때문에 ReqAttack 사이 1100ms Delay 사용

---

## 수정된 파일 목록 (이 세션 전체)

### 신규 생성
- `GameServer/Network/WebSocketFrameHandler.cs`
- `GameServer/Network/WsPipelineInitializer.cs`
- `GameServer/Network/WsServerBootstrap.cs`
- `GameServer/Component/Player/CharacterComponent.cs`
- `GameServer/Component/Player/PlayerWorldComponent.cs`
- `GameServer/Component/Room/MonsterComponent.cs`
- `GameServer/Component/Room/GameSessionComponent.cs`
- `GameServer/Systems/MonsterSystem.cs`
- `GameServer/Controllers/PlayerRpgController.cs`
- `GameServer.Database/Rows/CharacterRow.cs`
- `GameServer.Database/DbSet/CharacterDbSet.cs`
- `GameClient.Web/` (전체 프로젝트)
  - `Program.cs`, `appsettings.json`, `GameClient.Web.csproj`
  - `wwwroot/index.html`, `wwwroot/css/style.css`
  - `wwwroot/js/game.js`, `wwwroot/js/proto-bundle.json`
- `TestClient/Scenarios/RpgRoomScenario.cs`
- `TestClient/Scenarios/PveStressScenario.cs`

### 수정
- `GameServer/Component/Room/RoomComponent.cs` — GameSession 통합, BroadcastPacket, GetPlayers
- `GameServer/Component/Player/PlayerComponent.cs` — Character/World 컴포넌트 추가
- `GameServer/Network/LoginProcessor.cs` — CharacterComponent 초기화 + ResCharacterInfo 전송
- `GameServer/Network/GamePacketExtensions.cs` — RPG 패킷 라우팅 추가
- `GameServer/Systems/GameSystems.cs` — lobbyCount 1로 변경, MonsterSystem 초기화
- `GameServer.Database/System/GameDbContext.cs` — CharacterDbSet 추가
- `db/schema_game.sql` — characters 테이블 추가
- `TestClient/Program.cs` — rpg-room, pve-stress 시나리오 추가

---

## 실행 방법

```bash
# 서버 실행 (TCP:7777 + WebSocket:7778)
dotnet run --project GameServer

# 웹 클라이언트 (http://localhost:8081)
dotnet run --project GameClient.Web

# TestClient 시나리오
dotnet run --project TestClient -- --scenario rpg-room --clients 2 --delay 100
dotnet run --project TestClient -- --scenario pve-stress --clients 10 --delay 100

# DB 스키마 재적용 (MySQL)
mysql -u root -p gameserver < db/schema_game.sql
mysql -u root -p gamelog < db/schema_log.sql
```

---

## 미완성 작업 / 알려진 이슈

### 6.3 Web REST API 확인 (수동)
- `GameServer/Web/` 에 ASP.NET Core REST API 존재 (별도 포트)
- 엔드포인트 목록 확인 및 정상 응답 검증 필요

### 알려진 제한사항
- 서버는 단일 로비(lobbyCount=1)만 운영 — 스트레스 테스트 시 로비 병목 가능
- 몬스터 위치는 고정값 (랜덤 스폰 없음): Slime(200,150), Slime(600,150), Orc(400,350), Dragon(400,500)
- 캐릭터 저장: 연결 해제 시만 저장 (레벨업 즉시 저장은 구현되지 않음)
- 게임 종료 후 플레이어가 룸에 남아있음 — 클라이언트가 `ReqRoomExit` 전송해야 로비 복귀

---

## 요구사항 요약 (원본)

- 기존 DotNetty + Protocol Buffers 유지
- 로비/룸 시스템 **유지** — 유저가 룸을 직접 생성하고 입장
- 멀티플레이어: **PvE 전용** (플레이어 간 전투 없음)
- 룸당 최대 **2인**
- 브라우저에서 플레이 가능한 간단한 웹 RPG
