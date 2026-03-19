using System.Collections.Concurrent;
using Common.Logging;
using Common.Server;
using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Protocol;

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

    public IReadOnlyList<PlayerComponent> GetAll() => _players.Values.ToList();

    // 전체 접속자에게 시스템 공지 전송
    public void BroadcastAll(string message)
    {
        var noti = new GamePacket { NotiSystem = new NotiSystem { Message = message } };

        foreach (var player in _players.Values)
        {
            _ = player.Session.SendAsync(noti);
        }
    }

    // DisconnectAsync가 PlayerSystem.Remove()를 DB write 이후에 호출하므로,
    // Count == 0은 모든 플레이어의 DB 로그아웃 저장 완료를 의미함
    public async Task WaitUntilEmptyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_players.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        if (_players.Count > 0)
        {
            GameLogger.Warn("PlayerSystem", $"WaitUntilEmptyAsync 타임아웃: {_players.Count}명 미정리");
        }
    }

    public void Initialize(int maxPlayers) => MaxPlayers = maxPlayers;

    public void StartSystem() => _workers.StartSystem();

    public void Stop() => _workers.Stop();
}
