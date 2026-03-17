using Common.Logging;
using GameServer.Protocol;

namespace Common.Server.Routing;

public class RouterBuilder
{
    private readonly List<IRouter> _routers = new();

    // 응답 없는 핸들러
    public RouterBuilder With<TReq>(Action<TReq> handler) where TReq : class
    {
        CheckDuplicate<TReq>();
        _routers.Add(new PacketRouter<TReq>(handler));
        return this;
    }

    // 응답 있는 핸들러 — 응답은 항상 GamePacket
    public RouterBuilder With<TReq>(Func<TReq, GamePacket> handler) where TReq : class
    {
        CheckDuplicate<TReq>();
        _routers.Add(new PacketRouterWithResponse<TReq>(handler));
        return this;
    }

    public IReadOnlyList<IRouter> Build() => _routers;

    private void CheckDuplicate<TReq>()
    {
        if (_routers.Exists(r => r.GetRequestType() == typeof(TReq)))
        {
            throw new InvalidOperationException($"[RouterBuilder] 중복 라우터 등록: {typeof(TReq).Name}");
        }
    }
}