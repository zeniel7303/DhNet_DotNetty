using GameServer.Protocol;

namespace Common.Server.Routing;

public class PacketRouter<TReq>(Action<TReq> handler) : IRouter where TReq : class
{
    public void Handle(object request, RouterCallback callback)
    {
        handler((TReq)request);
        callback(null);
    }

    public Type GetRequestType() => typeof(TReq);
}

public class PacketRouterWithResponse<TReq>(Func<TReq, GamePacket> handler) : IRouter where TReq : class
{
    public void Handle(object request, RouterCallback callback)
        => callback(handler((TReq)request));

    public Type GetRequestType() => typeof(TReq);
}