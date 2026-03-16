using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Controllers;

public static class RoomController
{
    public static void HandleChat(GameSession session, ReqRoomChat req)
    {
        var player = session.Player;
        if (player?.CurrentRoom == null) return;
        if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 500) return;
        player.CurrentRoom.Chat(player, req.Message);
    }

    public static void HandleExit(GameSession session, ReqRoomExit req)
    {
        var player = session.Player;
        if (player?.CurrentRoom == null) return;
        player.CurrentRoom.Leave(player, false);
        // Lobby.Enter는 Room.Leave() JobQueue 람다 내부에서 호출됨
    }
}
