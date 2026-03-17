using Common.Logging;
using GameServer.Component.Player;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Network;

internal static class LoginProcessor
{
    public static async Task ProcessAsync(SessionComponent session, ReqLogin req)
    {
        if (session.Player != null)
        {
            GameLogger.Warn("Login", $"중복 로그인 시도: {session.Player.Name}");
            return;
        }

        if (PlayerSystem.Instance.Count >= PlayerSystem.Instance.MaxPlayers)
        {
            GameLogger.Warn("Login", $"서버 정원 초과 ({PlayerSystem.Instance.MaxPlayers}명) — 로그인 거부");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty }
            });
            return;
        }

        var player = new PlayerComponent(session, req.PlayerName);
        var loginAt = DateTime.UtcNow;
        var ip = session.Channel.RemoteAddress?.ToString();

        try
        {
            await DatabaseSystem.Instance.Game.Players.InsertAsync(new PlayerRow
            {
                player_id   = player.PlayerId,
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

        // DB Insert 완료 플래그 — DisconnectAsync에서 UpdateLogout 실행 허용
        player.MarkDbInserted();

        // DB await 중 연결 해제 확인
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
        // tcs 취소 경로: InternalPlayerGameEnter에서 _sessions 미존재 또는 IsDisconnected 감지 시 TrySetCanceled
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionSystem.Instance.EnqueuePlayerGameEnter(session, tcs);

        try
        {
            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            GameLogger.Warn("Login", $"로그인 중 연결 해제 (PlayerGameEnter 대기 중): {player.Name}");
            return;
        }
        catch (Exception ex)
        {
            GameLogger.Error("Login", $"PlayerGameEnter 오류: {player.Name}", ex);
            player.ImmediateFinalize();
            return;
        }

        var defaultLobby = LobbySystem.Instance.GetDefaultLobby();
        if (defaultLobby == null)
        {
            GameLogger.Warn("Login", $"로비 자동 배정 불가 — 모든 로비 만원: {player.Name}");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty }
            });
            player.DisconnectForNextTick();
            return;
        }

        // 로비 입장을 워커 스레드에서 실행하고 성공 여부를 반환받아 대기
        // → false 반환 시 ResLogin 전송 없이 종료 (플레이어는 DisconnectForNextTick으로 정리됨)
        var lobbyEntered = await player.EnqueueEventAsync(() =>
        {
            if (defaultLobby.TryEnter(player)) return true;

            // GetDefaultLobby 체크 이후 로비가 꽉 찬 경우 — 다른 로비 재시도
            var fallback = LobbySystem.Instance.GetDefaultLobby();
            if (fallback != null && fallback.TryEnter(player)) return true;

            // 모든 로비 만원 — 강제 종료 (ResLogin 전송 안 함)
            GameLogger.Warn("Login", $"로비 TryEnter 실패 (경쟁 조건) — 강제 종료: {player.Name}");
            player.DisconnectForNextTick();
            return false;
        });

        if (!lobbyEntered) return;

        GameLogger.Info("Login", $"로그인 성공: {player.Name} (Id={player.PlayerId})");

        // 로비 입장 완료 후 ResLogin 전송
        await session.SendAsync(new GamePacket
        {
            ResLogin = new ResLogin { PlayerId = player.PlayerId, PlayerName = player.Name }
        });

        DatabaseSystem.Instance.GameLog.LoginLogs.InsertAsync(new LoginLogRow
        {
            player_id   = player.PlayerId,
            player_name = player.Name,
            ip_address  = ip,
            login_at    = loginAt
        }).FireAndForget("Login");
    }
}
