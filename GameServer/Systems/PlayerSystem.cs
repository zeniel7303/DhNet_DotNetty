using System.Collections.Concurrent;
using Common.Logging;
using Common.Server;
using Common.Server.Component;
using GameServer.Component.Player;

namespace GameServer.Systems;

public class PlayerSystem
{
    public static readonly PlayerSystem Instance = new();

    public int MaxPlayers { get; private set; } = ServerConstants.MaxPlayers;

    private readonly WorkerSystem<PlayerComponent> _workers = new(workerCount: 2, intervalMs: 100);
    private readonly ConcurrentDictionary<ulong, PlayerComponent> _players = new();

    public int Count => _players.Count;

    public void Add(PlayerComponent player)
    {
        if (!_players.TryAdd(player.PlayerId, player))
        {
            GameLogger.Error("PlayerSystem", $"중복 PlayerId 감지: {player.PlayerId} — workers 등록 건너뜀");
            return;
        }
        _workers.Add(player);
    }

    public void Remove(PlayerComponent player)
    {
        _players.TryRemove(player.PlayerId, out _);
        _workers.Remove(player);
        // worker 제거 후 Dispose 위임 — DisconnectForNextTick 경로에서 Dispose 자동 완결
        player.Dispose();
    }

    public PlayerComponent? TryGet(ulong id) => _players.GetValueOrDefault(id);

    public void Initialize(int maxPlayers) => MaxPlayers = maxPlayers;

    public void StartSystem() => _workers.StartSystem();

    public void Stop() => _workers.Stop();
}
