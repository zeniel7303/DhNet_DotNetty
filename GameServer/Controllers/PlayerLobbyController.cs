using Common.Server.Routing;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Controllers;

public class PlayerLobbyController(PlayerComponent player) : PlayerBaseController(player)
{
    public override IReadOnlyList<IRouter> Routes() =>
        NewRouter()
            .With<ReqRoomList>(OnRoomList)
            .With<ReqCreateRoom>(OnCreateRoom)
            .With<ReqRoomEnter>(OnRoomEnter)
            .Build();

    private void OnRoomList(ReqRoomList req)     => Player.Lobby.RoomList(req);
    private void OnCreateRoom(ReqCreateRoom req) => Player.Lobby.CreateRoom(req);
    private void OnRoomEnter(ReqRoomEnter req)   => Player.Lobby.RoomEnter(req);
}
