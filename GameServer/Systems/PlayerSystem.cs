using System.Collections.Concurrent;
using GameServer.Entities;

namespace GameServer.Systems;

public class PlayerSystem
{
    public static readonly PlayerSystem Instance = new();

    private static int _maxPlayers = 100;
    public static int MaxPlayers => _maxPlayers;

    public static void Configure(int maxPlayers) => _maxPlayers = maxPlayers;

    private readonly ConcurrentDictionary<ulong, Player> _players = new();

    public int Count => _players.Count;

    public void Add(Player player) => _players.TryAdd(player.Id, player);

    public void Remove(Player player) => _players.TryRemove(player.Id, out _);

    public Player? TryGet(ulong id) => _players.TryGetValue(id, out var player) ? player : null;
}
