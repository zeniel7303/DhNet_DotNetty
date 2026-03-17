using Common.Server.Routing;
using GameServer.Component.Player;

namespace GameServer.Controllers;

// 생성자 주입 — null 안전성 확보
public abstract class PlayerBaseController(PlayerComponent player)
{
    protected PlayerComponent Player { get; } = player;

    public abstract IReadOnlyList<IRouter> Routes();

    protected RouterBuilder NewRouter() => new();
}
