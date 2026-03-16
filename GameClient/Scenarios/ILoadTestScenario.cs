using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameServer.Protocol;

namespace GameClient.Scenarios;

public interface ILoadTestScenario
{
    Task OnConnectedAsync(IChannel channel, ClientContext ctx);
    Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet);
    void OnDisconnected(ClientContext ctx);
}
