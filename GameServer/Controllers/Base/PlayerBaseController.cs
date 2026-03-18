using Common.Server.Routing;
using GameServer.Component.Player;

namespace GameServer.Controllers;

public abstract class PlayerBaseController(PlayerComponent player)
{
    protected PlayerComponent Player { get; } = player;

    public abstract IReadOnlyList<IRouter> Routes();

    protected RouterBuilder NewRouter() => new();
}
