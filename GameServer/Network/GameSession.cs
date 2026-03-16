using DotNetty.Transport.Channels;
using GameServer.Entities;
using GameServer.Protocol;

namespace GameServer.Network;

public class GameSession
{
    public IChannel Channel { get; }
    public Player? Player { get; set; }

    public GameSession(IChannel channel)
    {
        Channel = channel;
    }

    public async Task SendAsync(GamePacket packet)
    {
        await Channel.WriteAndFlushAsync(packet);
    }
}
