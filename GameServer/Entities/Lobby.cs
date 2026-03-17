using System.Threading.Channels;
using Common.Logging;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Network;
using GameServer.Protocol;

namespace GameServer.Entities;

public class Lobby
{
    private readonly Channel<Func<Task>> _jobChannel = Channel.CreateUnbounded<Func<Task>>();
    private readonly List<Player> _players = new();
    private int _playerCount;
    public int PlayerCount => _playerCount;

    public Lobby() => _ = Task.Run(ProcessJobsAsync);

    private async Task ProcessJobsAsync()
    {
        await foreach (var job in _jobChannel.Reader.ReadAllAsync())
        {
            try
            {
                await job();
            }
            catch (Exception ex)
            {
                GameLogger.Error("Lobby", "JobQueue 오류", ex);
            }
        }
    }

    private void DoAsync(Func<Task> job) => _jobChannel.Writer.TryWrite(job);

    private static async Task SafeSendAsync(Player p, GamePacket packet)
    {
        try { await p.Session.SendAsync(packet); }
        catch { /* 연결 해제된 클라이언트 무시 */ }
    }

    public void Enter(Player player) => DoAsync(() =>
    {
        _players.Add(player);
        _playerCount = _players.Count;
        GameLogger.Info("Lobby", $"입장: {player.Name} (현재 {_players.Count}명)");
        return Task.CompletedTask;
    });

    public void Leave(Player player) => DoAsync(() =>
    {
        if (!_players.Remove(player))
        {
            GameLogger.Warn("Lobby", $"퇴장 요청: {player.Name} — 목록에 없음 (무시됨)");
        }
        else
        {
            _playerCount = _players.Count;
            GameLogger.Info("Lobby", $"퇴장: {player.Name} (현재 {_players.Count}명)");
        }
        return Task.CompletedTask;
    });

    public void Chat(Player sender, string message) => DoAsync(async () =>
    {
        GameLogger.Info("Lobby", $"채팅: {sender.Name}: {message}");
        DatabaseSystem.Instance.GameLog.ChatLogs.InsertAsync(new ChatLogRow
        {
            player_id  = sender.Id,
            room_id    = null,
            channel    = "lobby",
            message    = message,
            created_at = DateTime.UtcNow
        }).FireAndForget("Lobby");
        var noti = new GamePacket
        {
            NotiLobbyChat = new NotiLobbyChat
            {
                PlayerId = sender.Id,
                PlayerName = sender.Name,
                Message = message
            }
        };
        await Task.WhenAll(_players.Select(p => SafeSendAsync(p, noti)));
    });
}
