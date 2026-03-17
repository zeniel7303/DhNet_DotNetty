using Common.Server.Routing;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Controllers;

public class PlayerHeartbeatController(PlayerComponent player) : PlayerBaseController(player)
{
    public override IReadOnlyList<IRouter> Routes() =>
        NewRouter()
            .With<ReqHeartbeat>(_ => new GamePacket { ResHeartbeat = new ResHeartbeat() })
            .Build();
}
