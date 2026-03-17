using Common.Server.Routing;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Controllers;

public class PlayerLobbyController(PlayerComponent player) : PlayerBaseController(player)
{
    public override IReadOnlyList<IRouter> Routes() =>
        NewRouter()
            .With<ReqLobbyList>(OnLobbyList)
            .With<ReqLobbyChat>(OnChat)
            .With<ReqRoomEnter>(OnRoomEnter)
            .Build();

    private void OnLobbyList(ReqLobbyList req) => Player.Lobby.LobbyList(req);
    private void OnChat(ReqLobbyChat req)      => Player.Lobby.Chat(req);
    private void OnRoomEnter(ReqRoomEnter req) => Player.Lobby.RoomEnter(req);
}
