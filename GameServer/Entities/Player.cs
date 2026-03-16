using Common.Logging;
using GameServer.Database;
using GameServer.Network;
using GameServer.Systems;

namespace GameServer.Entities;

public class Player
{
    public ulong Id { get; }
    public string Name { get; }
    public GameSession Session { get; }
    public Room? CurrentRoom { get; set; }

    private int _disconnected = 0;

    public Player(GameSession session, string name = "")
    {
        Id = IdGenerators.Player.Next();
        Name = string.IsNullOrEmpty(name) ? "TempUser" + Id : name;
        Session = session;
    }

    public async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1)
            return;

        if (CurrentRoom != null)
        {
            CurrentRoom.Leave(this, true);
        }
        else
        {
            LobbySystem.Instance.Lobby.Leave(this);
        }
        PlayerSystem.Instance.Remove(this);

        var logoutAt = DateTime.UtcNow;
        try
        {
            await DatabaseSystem.Instance.Game.Players.UpdateLogoutAsync(Id, logoutAt);
        }
        catch (Exception ex)
        {
            GameLogger.Error("Player", $"플레이어 로그아웃 DB 저장 실패: {Id}", ex);
        }
        DatabaseSystem.Instance.GameLog.LoginLogs.UpdateLogoutAsync(Id, logoutAt).FireAndForget("Player");
    }
}
