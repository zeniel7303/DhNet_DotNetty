using GameServer.Protocol;

namespace Common.Server.Routing;

// 응답 없는 라우터
public class PacketRouter<TReq>(Action<TReq> handler) : IRouter where TReq : class
{
    public void Handle(object request, RouterCallback callback)
    {
        handler((TReq)request);
        callback(null);
    }

    public Type GetRequestType() => typeof(TReq);
}

// 응답 있는 라우터 — 응답 타입은 항상 GamePacket
public class PacketRouterWithResponse<TReq>(Func<TReq, GamePacket> handler) : IRouter where TReq : class
{
    public void Handle(object request, RouterCallback callback)
        => callback(handler((TReq)request));

    public Type GetRequestType() => typeof(TReq);
}