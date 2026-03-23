# MessagePack Serialization — Tasks

Last Updated: 2026-03-23

## Phase 1: Protocol 레이어
- [ ] GameServer.Protocol.csproj 의존성 교체
- [ ] Messages/ErrorCode.cs
- [ ] Messages/Login.cs
- [ ] Messages/Register.cs
- [ ] Messages/Room.cs
- [ ] Messages/Lobby.cs
- [ ] Messages/Heartbeat.cs
- [ ] Messages/SystemMessages.cs
- [ ] GamePacket.cs (PacketType + IPacketPayload + GamePacket)
- [ ] Serialization/ISerializer.cs
- [ ] Serialization/MessagePackGameSerializer.cs
- [ ] Codecs/GamePacketDecoder.cs
- [ ] Codecs/GamePacketEncoder.cs

## Phase 2: GameServer
- [ ] GameServer.csproj
- [ ] GamePipelineInitializer.cs
- [ ] IPacketPolicy.cs
- [ ] PacketPairPolicy.cs
- [ ] PacketRatePolicy.cs
- [ ] PacketHandshakePolicy.cs
- [ ] SessionComponent.cs
- [ ] GameServerHandler.cs
- [ ] GamePacketExtensions.cs
- [ ] LoginProcessor.cs
- [ ] RegisterProcessor.cs
- [ ] RoomComponent.cs
- [ ] LobbyComponent.cs
- [ ] PlayerLobbyComponent.cs
- [ ] PlayerComponent.cs
- [ ] PlayerSystem.cs
- [ ] PlayerHeartbeatController.cs

## Phase 3: GameClient
- [ ] GameClient.csproj
- [ ] Program.cs
- [ ] ClientContext.cs
- [ ] GameClientHandler.cs
- [ ] BaseRoomScenario.cs
- [ ] RoomScenario.cs
- [ ] RoomOnceScenario.cs
- [ ] RoomLoopScenario.cs
- [ ] RoomChatScenario.cs
- [ ] LobbyChatScenario.cs
- [ ] ReconnectStressScenario.cs
- [ ] DuplicateLoginScenario.cs

## Phase 4: 검증
- [ ] dotnet build (전체)
- [ ] 빌드 오류 수정
- [ ] git commit
