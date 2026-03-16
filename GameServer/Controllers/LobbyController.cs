using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Controllers;

public static class LobbyController
{
    public static void HandleChat(GameSession session, ReqLobbyChat req)
    {
        var player = session.Player;
        if (player == null || player.CurrentRoom != null) return;
        if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 500) return;
        LobbySystem.Instance.Lobby.Chat(player, req.Message);
    }

    public static void HandleRoomEnter(GameSession session, ReqRoomEnter req)
    {
        var player = session.Player;
        if (player == null || player.CurrentRoom != null) return;
        var room = LobbySystem.Instance.GetOrCreateRoom();
        LobbySystem.Instance.Lobby.Leave(player);
        room.Enter(player);
    }
}
