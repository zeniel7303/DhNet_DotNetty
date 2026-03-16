using Common.Logging;
using GameServer.Network;
using GameServer.Protocol;

namespace GameServer.Controllers;

public static class PacketRouter
{
    public static void Dispatch(GameSession session, GamePacket packet)
    {
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.ReqLogin:
                _ = LoginController.HandleAsync(session, packet.ReqLogin);
                break;
            case GamePacket.PayloadOneofCase.ReqLobbyChat:
                LobbyController.HandleChat(session, packet.ReqLobbyChat);
                break;
            case GamePacket.PayloadOneofCase.ReqRoomEnter:
                LobbyController.HandleRoomEnter(session, packet.ReqRoomEnter);
                break;
            case GamePacket.PayloadOneofCase.ReqRoomChat:
                RoomController.HandleChat(session, packet.ReqRoomChat);
                break;
            case GamePacket.PayloadOneofCase.ReqRoomExit:
                RoomController.HandleExit(session, packet.ReqRoomExit);
                break;
            case GamePacket.PayloadOneofCase.ReqHeartbeat:
                _ = session.SendAsync(new GamePacket { ResHeartbeat = new ResHeartbeat() });
                break;
            default:
                GameLogger.Warn("PacketRouter", $"미처리 패킷: {packet.PayloadCase}");
                break;
        }
    }
}
