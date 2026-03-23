# MessagePack Serialization — Context

Last Updated: 2026-03-23

## Branch
`feature/messagepack-serialization`

## Key Files

### 교체 대상 (삭제/수정)
- `GameServer.Protocol/Protos/*.proto` (8개) — 삭제
- `GameServer.Protocol/GameServer.Protocol.csproj` — protobuf 의존성 제거, MessagePack 추가
- `GameServer/GameServer.csproj` — `DotNetty.Codecs.Protobuf` 제거
- `GameClient/GameClient.csproj` — `DotNetty.Codecs.Protobuf` 제거

### 신규 생성
- `GameServer.Protocol/Messages/*.cs` (7개)
- `GameServer.Protocol/GamePacket.cs`
- `GameServer.Protocol/Serialization/ISerializer.cs`
- `GameServer.Protocol/Serialization/MessagePackGameSerializer.cs`
- `GameServer.Protocol/Codecs/GamePacketDecoder.cs`
- `GameServer.Protocol/Codecs/GamePacketEncoder.cs`

### 마이그레이션 대상
- `GameServer/Network/GamePipelineInitializer.cs`
- `GameServer/Network/GameServerHandler.cs`
- `GameServer/Network/GamePacketExtensions.cs`
- `GameServer/Network/SessionComponent.cs`
- `GameServer/Network/LoginProcessor.cs`
- `GameServer/Network/RegisterProcessor.cs`
- `GameServer/Network/Policies/IPacketPolicy.cs`
- `GameServer/Network/Policies/PacketPairPolicy.cs`
- `GameServer/Network/Policies/PacketRatePolicy.cs`
- `GameServer/Network/Policies/PacketHandshakePolicy.cs`
- `GameServer/Component/Room/RoomComponent.cs`
- `GameServer/Component/Lobby/LobbyComponent.cs`
- `GameServer/Component/Player/PlayerLobbyComponent.cs`
- `GameServer/Component/Player/PlayerComponent.cs`
- `GameServer/Systems/PlayerSystem.cs`
- `GameServer/Controllers/PlayerHeartbeatController.cs`
- `GameClient/Program.cs`
- `GameClient/Controllers/ClientContext.cs`
- `GameClient/Network/GameClientHandler.cs`
- `GameClient/Scenarios/*.cs` (8개)

## PacketType 값 매핑 (proto 필드 번호와 동일하게 유지)
```
None=0, ReqLogin=1, ResLogin=2,
ReqRoomEnter=3, ResRoomEnter=4, NotiRoomEnter=5,
ReqRoomChat=6, NotiRoomChat=7,
ReqRoomExit=8, ResRoomExit=9, NotiRoomExit=10,
ReqLobbyChat=11, NotiLobbyChat=12,
ReqHeartbeat=13, ResHeartbeat=14,
ReqLobbyList=15, ResLobbyList=16,
NotiSystem=17, ReqRegister=18, ResRegister=19
```

## 의존성
- `MessagePack` NuGet 패키지 (v2.x)
- `DotNetty.Codecs` (기존 유지)
- `DotNetty.Codecs.Protobuf` 제거
