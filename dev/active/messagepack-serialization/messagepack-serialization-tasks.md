# MessagePack Serialization — Tasks

Last Updated: 2026-03-23

## Phase 1: Protocol 레이어
- ✅ GameServer.Protocol.csproj 의존성 교체
- ✅ Messages/ErrorCode.cs
- ✅ Messages/Login.cs
- ✅ Messages/Register.cs
- ✅ Messages/Room.cs
- ✅ Messages/Lobby.cs
- ✅ Messages/Heartbeat.cs
- ✅ Messages/SystemMessages.cs
- ✅ GamePacket.cs (PacketType + IPacketPayload + GamePacket)
- ✅ Serialization/ISerializer.cs
- ✅ Serialization/MessagePackGameSerializer.cs
- ✅ Codecs/GamePacketDecoder.cs
- ✅ Codecs/GamePacketEncoder.cs

## Phase 2: GameServer
- ✅ GameServer.csproj
- ✅ GamePipelineInitializer.cs
- ✅ IPacketPolicy.cs
- ✅ PacketPairPolicy.cs
- ✅ PacketRatePolicy.cs
- ✅ PacketHandshakePolicy.cs
- ✅ SessionComponent.cs
- ✅ GameServerHandler.cs
- ✅ GamePacketExtensions.cs
- ✅ LoginProcessor.cs
- ✅ RegisterProcessor.cs
- ✅ RoomComponent.cs
- ✅ LobbyComponent.cs
- ✅ PlayerLobbyComponent.cs
- ✅ PlayerComponent.cs
- ✅ PlayerSystem.cs
- ✅ PlayerHeartbeatController.cs

## Phase 3: GameClient
- ✅ GameClient.csproj
- ✅ Program.cs
- ✅ ClientContext.cs
- ✅ GameClientHandler.cs
- ✅ BaseRoomScenario.cs
- ✅ RoomScenario.cs
- ✅ RoomOnceScenario.cs
- ✅ RoomLoopScenario.cs
- ✅ RoomChatScenario.cs
- ✅ LobbyChatScenario.cs
- ✅ ReconnectStressScenario.cs
- ✅ DuplicateLoginScenario.cs

## Phase 4: 검증
- ✅ dotnet build (전체) — 경고 0, 오류 0
- ✅ git commit (45c237a) — 마이그레이션
- ✅ 코드 리뷰 P2 수정 — commit f8a5914
  - ✅ GamePacketDecoder 로거 의존성 오류 수정 (Trace.TraceWarning으로 교체)
  - ✅ ResLogin.PlayerId 주석 추가 (실제 account_id 값임 명시)
  - ✅ GamePacket.cs 3-place sync rule 주석 블록 추가
- ✅ dotnet build (재확인) — 경고 0, 오류 0

## 선택적 후속 작업
- [ ] 실제 서버-클라이언트 통신 테스트
- [ ] MessagePack Source Generator 적용 (리플렉션 → 코드젠)
- [ ] ProtobufSerializer 구현 및 벤치마크 비교
- [ ] PR 생성: feature/messagepack-serialization → main
