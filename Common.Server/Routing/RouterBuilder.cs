using Common.Logging;

namespace Common.Server.Routing;

public class RouterBuilder
{
    private readonly List<IRouter> _routers = new();

    public RouterBuilder With<TReq>(Action<TReq> handler) where TReq : class
    {
        CheckDuplicate<TReq>();
        _routers.Add(new PacketRouter<TReq>(handler));
        return this;
    }

    public RouterBuilder With<TReq, TResponse>(Func<TReq, TResponse> handler)
        where TReq : class
        where TResponse : class
    {
        CheckDuplicate<TReq>();
        _routers.Add(new PacketRouterWithResponse<TReq, TResponse>(handler));
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
