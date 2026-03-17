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
    public static async Task HandleAsync(SessionComponent session, ReqLogin req)
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

        // DB await 중 연결 해제 확인 (Interlocked 플래그 기준 — Channel.Active보다 신뢰성 높음)
        if (session.IsDisconnected)
        {
            GameLogger.Warn("Login", $"로그인 중 연결 해제 (DB 완료 후): {player.Name}");
            player.ImmediateFinalize();
            return;
        }

        // PlayerCreated: SessionSystem이 AttachPlayer 수행
        // PlayerGameEnter보다 먼저 큐에 적재 — FIFO 순서로 Attach 후 Add 보장
        SessionSystem.Instance.EnqueuePlayerCreated(session, player);

        // PlayerGameEnter: SessionSystem이 PlayerSystem.Add + SetEntryHandshakeCompleted 수행
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SetPendingTcs(tcs);
        SessionSystem.Instance.EnqueuePlayerGameEnter(session, tcs);

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            // CancelPendingTcs에 의한 취소 — InternalDisconnectSession이 player 정리 담당
            GameLogger.Warn("Login", $"로그인 중 연결 해제 (PlayerGameEnter 대기 중): {player.Name}");
            return;
        }
        catch (Exception ex)
        {
            GameLogger.Error("Login", $"PlayerGameEnter 오류: {player.Name}", ex);
            player.ImmediateFinalize();
            return;
        }
        finally
        {
            session.SetPendingTcs(null);
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
