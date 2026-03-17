using GameServer.Protocol;

namespace Common.Server.Routing;

public delegate void RouterCallback(GamePacket? response);

public interface IRouter
{
    void Handle(object request, RouterCallback callback);
    Type GetRequestType();
}