namespace Common.Server.Routing;

public delegate void RouterCallback(object? response);

public interface IRouter
{
    void Handle(object request, RouterCallback callback);
    Type GetRequestType();
}
