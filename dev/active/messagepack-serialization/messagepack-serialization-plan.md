# MessagePack Serialization Migration Plan

Last Updated: 2026-03-23

## Executive Summary

Protocol Buffers → MessagePack-CSharp 교체 작업.
동시에 직렬화 레이어를 `ISerializer` 추상화로 분리하여 추후 직렬화 방식 교체를 용이하게 한다.

## Current State Analysis

- `GameServer.Protocol`: `.proto` 파일 → Grpc.Tools 코드 생성 → Google.Protobuf 런타임
- `GamePacket`: protobuf `oneof payload` → `PayloadOneofCase` enum + 타입별 프로퍼티 접근
- 파이프라인: `ProtobufDecoder(GamePacket.Parser)` + `ProtobufEncoder()`
- 의존성: `Google.Protobuf`, `Grpc.Tools`, `DotNetty.Codecs.Protobuf`

## Proposed Future State

```
GameServer.Protocol/
├── Messages/
│   ├── ErrorCode.cs         (enum)
│   ├── Login.cs             (ReqLogin, ResLogin)
│   ├── Register.cs          (ReqRegister, ResRegister)
│   ├── Room.cs              (ReqRoomEnter, ResRoomEnter, NotiRoomEnter, ...)
│   ├── Lobby.cs             (ReqLobbyChat, NotiLobbyChat, ReqLobbyList, ResLobbyList, LobbyInfo)
│   ├── Heartbeat.cs         (ReqHeartbeat, ResHeartbeat)
│   └── SystemMessages.cs    (NotiSystem)
├── GamePacket.cs            (PacketType enum + GamePacket 클래스 + IPacketPayload union)
├── Serialization/
│   ├── ISerializer.cs       (추상화 인터페이스)
│   └── MessagePackGameSerializer.cs
└── Codecs/
    ├── GamePacketDecoder.cs  (DotNetty MessageToMessageDecoder)
    └── GamePacketEncoder.cs  (DotNetty MessageToByteEncoder)
```

### 핵심 API 변경

| 기존 (Protobuf) | 신규 (MessagePack) |
|---|---|
| `GamePacket.PayloadOneofCase` | `PacketType` enum |
| `packet.PayloadCase` | `packet.Type` |
| `packet.ReqLogin` | `packet.As<ReqLogin>()` |
| `new GamePacket { ReqLogin = ... }` | `GamePacket.From(new ReqLogin(...))` |
| `ProtobufDecoder(GamePacket.Parser)` | `new GamePacketDecoder(serializer)` |
| `ProtobufEncoder()` | `new GamePacketEncoder(serializer)` |

## Implementation Phases

### Phase 1: Protocol 레이어 재작성 (M)
1. `GameServer.Protocol.csproj` 의존성 교체
2. 메시지 클래스 작성 (`[MessagePackObject]`, `[Key]` 어노테이션)
3. `IPacketPayload` 인터페이스 + `[Union]` 어노테이션
4. `GamePacket` 클래스 + `PacketType` enum
5. `ISerializer` + `MessagePackGameSerializer`
6. `GamePacketDecoder` + `GamePacketEncoder`

### Phase 2: GameServer 마이그레이션 (L)
1. `GameServer.csproj` - `DotNetty.Codecs.Protobuf` 제거
2. `GamePipelineInitializer` - 코덱 교체
3. `IPacketPolicy` + `PacketPairPolicy` + `PacketRatePolicy` + `PacketHandshakePolicy` - `PayloadOneofCase` → `PacketType`
4. `SessionComponent` - `PayloadCase` → `Type`
5. `GameServerHandler` - switch 교체
6. `LoginProcessor`, `RegisterProcessor` - `new GamePacket {...}` → `GamePacket.From(...)`
7. `RoomComponent`, `LobbyComponent`, `PlayerLobbyComponent` - 동일
8. `PlayerSystem`, `PlayerHeartbeatController`, `GamePacketExtensions`, `PlayerComponent`

### Phase 3: GameClient 마이그레이션 (M)
1. `GameClient.csproj` - `DotNetty.Codecs.Protobuf` 제거
2. `Program.cs` - 파이프라인 교체
3. `ClientContext` - `new GamePacket {...}` → `GamePacket.From(...)`
4. `GameClientHandler`
5. 시나리오 파일 8개

## Risk Assessment

| 위험 | 영향 | 완화 |
|---|---|---|
| MessagePack Union 직렬화 오류 | High | 단위 테스트 또는 빌드 후 직접 확인 |
| `[Key]` 인덱스 충돌 | Medium | 파일별 명확한 Key 할당 |
| `IPacketPayload` null 역참조 | Low | `As<T>()` 내부에서 null 체크 |
