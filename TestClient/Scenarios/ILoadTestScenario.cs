using DotNetty.Transport.Channels;
using TestClient.Controllers;
using GameServer.Protocol;

namespace TestClient.Scenarios;

public interface ILoadTestScenario
{
    Task OnConnectedAsync(IChannel channel, ClientContext ctx);
    Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet);
    void OnDisconnected(ClientContext ctx);
}
