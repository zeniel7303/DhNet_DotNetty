# MessagePack Serialization — Context

Last Updated: 2026-03-23

## 상태: ✅ 완료 (커밋됨)

Branch: `feature/messagepack-serialization`
- Commit `45c237a` — feat: Protobuf → MessagePack 직렬화 교체 및 ISerializer 추상화
- Commit `f8a5914` — fix: P2 코드 리뷰 지적사항 반영 (로거 의존성, PlayerId 주석, 3-place sync rule)

---

## 이 세션에서 내린 주요 결정사항

### 1. ISerializer 추상화 계층
`GameServer.Protocol/Serialization/ISerializer.cs` — 직렬화 방식 교체 시 구현체만 바꾸면 됨.
```csharp
public interface ISerializer {
    byte[] Serialize(GamePacket packet);
    GamePacket? Deserialize(byte[] data);
}
```

### 2. DotNetty 코덱 위치
`GameServer.Protocol/Codecs/` 에 배치 — 서버/클라이언트 양쪽에서 공유하므로 Protocol 프로젝트가 적합.
`GameServer.Protocol.csproj`에 `DotNetty.Codecs` 패키지 추가.

### 3. IPacketPayload Union 설계
`IPacketPayload` 인터페이스에 MessagePack `[Union]` 어노테이션으로 19개 타입 등록.
Union 키 값 = 기존 proto 필드 번호와 동일하게 유지 (1~19).

### 4. PacketType enum
`GamePacket.PayloadOneofCase` 완전 대체. 기존 proto 번호와 동일한 값 사용.

### 5. GamePacket API
- `GamePacket.From<T>(payload)` — 팩토리 메서드, TypeMap으로 PacketType 자동 추론
- `packet.As<T>()` — Payload 캐스팅 헬퍼, null 시 InvalidOperationException
- `{ get; init; }` 프로퍼티 + `[Key]` 어노테이션

### 6. GamePipelineInitializer ISerializer 주입
생성자에 `ISerializer? serializer = null` 추가 → 미전달 시 `MessagePackGameSerializer.Instance` 사용.
추후 다른 직렬화 방식 테스트 시 주입만 하면 됨.

### 7. MessagePackSecurity.UntrustedData
게임 서버 특성상 외부 데이터를 역직렬화하므로 보안 옵션 적용.

### 8. GamePacketDecoder 로깅 (P2 수정)
`Microsoft.Extensions.Logging.Console`은 Protocol 프로젝트 의존성에 없으므로 추가 불가.
→ `System.Diagnostics.Trace.TraceWarning()` 으로 대체 (추가 패키지 불필요).

### 9. 3-place sync rule (P2 수정)
새 패킷 타입 추가 시 반드시 세 곳을 동기화:
1. `PacketType enum`
2. `IPacketPayload [Union]` 어트리뷰트
3. `GamePacket.TypeMap` 딕셔너리
→ `GamePacket.cs` 상단에 주석 블록으로 문서화.

---

## 수정된 파일 목록

### 신규 생성 (GameServer.Protocol)
- `GamePacket.cs` — PacketType enum + IPacketPayload Union + GamePacket 클래스
- `Messages/ErrorCode.cs`
- `Messages/Login.cs`
- `Messages/Register.cs`
- `Messages/Room.cs`
- `Messages/Lobby.cs`
- `Messages/Heartbeat.cs`
- `Messages/SystemMessages.cs`
- `Serialization/ISerializer.cs`
- `Serialization/MessagePackGameSerializer.cs`
- `Codecs/GamePacketDecoder.cs`
- `Codecs/GamePacketEncoder.cs`

### 삭제
- `Protos/*.proto` (8개 파일 + Protos 디렉토리)

### 수정 (GameServer)
- `GameServer.csproj` — DotNetty.Codecs.Protobuf 제거
- `Network/GamePipelineInitializer.cs` — 코덱 교체, ISerializer 주입
- `Network/GameServerHandler.cs` — PayloadCase → Type, packet.ReqXxx → packet.As<T>()
- `Network/GamePacketExtensions.cs` — ExtractPayload 업데이트
- `Network/SessionComponent.cs` — PayloadCase → Type
- `Network/LoginProcessor.cs` — new GamePacket{...} → GamePacket.From(...)
- `Network/RegisterProcessor.cs` — 동일
- `Network/Policies/IPacketPolicy.cs` — PayloadOneofCase → PacketType
- `Network/Policies/PacketPairPolicy.cs` — 동일
- `Network/Policies/PacketRatePolicy.cs` — 동일
- `Network/Policies/PacketHandshakePolicy.cs` — 동일
- `Component/Room/RoomComponent.cs`
- `Component/Lobby/LobbyComponent.cs`
- `Component/Player/PlayerLobbyComponent.cs`
- `Component/Player/PlayerComponent.cs` — packet.PayloadCase → packet.Type
- `Systems/PlayerSystem.cs`
- `Controllers/PlayerHeartbeatController.cs`

### 수정 (GameClient)
- `GameClient.csproj` — DotNetty.Codecs.Protobuf 제거
- `Program.cs` — 파이프라인 코덱 교체
- `Controllers/ClientContext.cs`
- `Network/GameClientHandler.cs`
- `Scenarios/BaseRoomScenario.cs`
- `Scenarios/RoomScenario.cs`
- `Scenarios/RoomOnceScenario.cs`
- `Scenarios/RoomLoopScenario.cs`
- `Scenarios/RoomChatScenario.cs`
- `Scenarios/LobbyChatScenario.cs`
- `Scenarios/ReconnectStressScenario.cs`
- `Scenarios/DuplicateLoginScenario.cs`

---

## 빌드 결과
```
경고 0개 / 오류 0개 — 빌드 성공 (f8a5914 이후)
```

---

## 다음 단계 (선택적 후속 작업)

1. **실제 서버-클라이언트 통신 테스트** — `dotnet run` 후 부하 테스트 시나리오 실행
2. **ProtobufSerializer 구현** — ISerializer를 구현하여 벤치마크 비교 가능
3. **Source Generator 적용** — MessagePack Source Generator로 성능 최적화 (현재는 리플렉션 기반)
4. **PR 생성** — `feature/messagepack-serialization` → `main`

---

## 발견된 특이사항

- `LobbySystem.GetLobbyList()` 반환 타입 `LobbyInfo[]` 은 변경 없음 — 프로퍼티명(LobbyId, PlayerCount, MaxCapacity, IsFull)이 동일하므로 LobbySystem.cs 수정 불필요
- `ResLobbyList.Lobbies = new List<LobbyInfo>()` 초기화로 기존 `res.Lobbies.Add(info)` 패턴 유지
- `LF → CRLF` 라인 엔딩 경고는 .gitattributes 미설정으로 인한 것 — 동작에 영향 없음
- `ResLogin.PlayerId` 필드는 실제로 `account_id` 값을 담음 — 클라이언트 호환성을 위해 이름 유지, 주석으로 명시
- `Microsoft.Extensions.Logging.Console` 은 Protocol 프로젝트에 불필요한 의존성이므로 추가 안 함 — Trace.TraceWarning 사용
