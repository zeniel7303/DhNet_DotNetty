using Common.Server.Routing;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Controllers;

public class PlayerRoomController(PlayerComponent player) : PlayerBaseController(player)
{
    public override IReadOnlyList<IRouter> Routes() =>
        NewRouter()
            .With<ReqRoomChat>(OnChat)
            .With<ReqRoomExit>(OnExit)
            .Build();

    private void OnChat(ReqRoomChat req) => Player.Room.Chat(req);
    private void OnExit(ReqRoomExit req) => Player.Room.Exit();
}
