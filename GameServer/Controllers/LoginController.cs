using Common.Logging;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Entities;
using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Controllers;

public static class LoginController
{
    public static async Task HandleAsync(GameSession session, ReqLogin req)
    {
        if (session.Player != null)
        {
            GameLogger.Warn("Login", $"중복 로그인 시도: {session.Player.Name}");
            return;
        }

        if (PlayerSystem.Instance.Count >= PlayerSystem.MaxPlayers)
        {
            GameLogger.Warn("Login", $"서버 정원 초과 ({PlayerSystem.MaxPlayers}명) — 로그인 거부");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty }
            });
            return;
        }
        var player = new Player(session, req.PlayerName);
        var loginAt = DateTime.UtcNow;
        var ip = session.Channel.RemoteAddress?.ToString();

        try
        {
            await DatabaseSystem.Instance.Game.Players.InsertAsync(new PlayerRow
            {
                player_id   = player.Id,
                player_name = player.Name,
                login_at    = loginAt,
                ip_address  = ip
            });
        }
        catch (Exception ex)
        {
            GameLogger.Error("Login", $"플레이어 DB 저장 실패: {player.Name}", ex);
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty }
            });
            return;
        }

        PlayerSystem.Instance.Add(player);
        session.Player = player;
        // Add↔session.Player 사이에 ChannelInactive가 발화하면 session.Player가 null이어서
        // DisconnectAsync()가 호출되지 않는다. 채널이 이미 닫혔다면 수동으로 정리한다.
        if (!session.Channel.Active)
        {
            _ = player.DisconnectAsync();
            return;
        }
        LobbySystem.Instance.Lobby.Enter(player);
        GameLogger.Info("Login", $"로그인 성공: {player.Name} (Id={player.Id})");
        await session.SendAsync(new GamePacket
        {
            ResLogin = new ResLogin { PlayerId = player.Id, PlayerName = player.Name }
        });
        DatabaseSystem.Instance.GameLog.LoginLogs.InsertAsync(new LoginLogRow
        {
            player_id   = player.Id,
            player_name = player.Name,
            ip_address  = ip,
            login_at    = loginAt
        }).FireAndForget("Login");
    }
}
