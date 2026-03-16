using System.Collections.Concurrent;
using DotNetty.Transport.Channels;
using GameServer.Network;

namespace GameServer.Systems;

public class GameSessionSystem
{
    public static readonly GameSessionSystem Instance = new();

    private readonly ConcurrentDictionary<IChannelId, GameSession> _sessions = new();

    public void Register(GameSession session) => _sessions.TryAdd(session.Channel.Id, session);

    public void Unregister(GameSession session) => _sessions.TryRemove(session.Channel.Id, out _);
}
