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
            GameLogger.Warn("Login", $"이미 로그인된 세션에서 ReqLogin 수신 — 연결 종료: {session.Player.Name}");
            await session.Channel.CloseAsync();
            return;
        }

        // 계정 인증 — 실패 시 에러 응답 전송 후 null 반환
        var account = await AuthenticateAsync(session, req.Username, req.Password);
        if (account == null) return;

        if (PlayerSystem.Instance.Count >= PlayerSystem.Instance.MaxPlayers)
        {
            GameLogger.Warn("Login", $"서버 정원 초과 ({PlayerSystem.Instance.MaxPlayers}명) — 로그인 거부");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty, ErrorCode = ErrorCode.ServerFull }
            });
            return;
        }

        // 플레이어 이름 = 계정 username (클라이언트 req.PlayerName 대신 DB 값 사용)
        var player = new PlayerComponent(session, account.username);
        var loginAt = DateTime.UtcNow;
        var ip = session.Channel.RemoteAddress?.ToString();

        try
        {
            await DatabaseSystem.Instance.Game.Players.InsertAsync(new PlayerRow
            {
                player_id   = player.PlayerId,
                player_name = player.Name,
                login_at    = loginAt,
                ip_address  = ip,
                account_id  = account.account_id
            });
        }
        catch (Exception ex)
        {
            GameLogger.Error("Login", $"플레이어 DB 저장 실패: {player.Name}", ex);
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty, ErrorCode = ErrorCode.DbError }
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
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty, ErrorCode = ErrorCode.LobbyFull }
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
            ResLogin = new ResLogin { PlayerId = player.PlayerId, PlayerName = player.Name, ErrorCode = ErrorCode.Success }
        });

        DatabaseSystem.Instance.GameLog.LoginLogs.InsertAsync(new LoginLogRow
        {
            player_id   = player.PlayerId,
            player_name = player.Name,
            ip_address  = ip,
            login_at    = loginAt
        }).FireAndForget("Login");
    }

    /// <summary>
    /// 계정 인증. 성공 시 AccountRow 반환, 실패 시 에러 응답 전송 후 null 반환.
    /// username 없거나 password 불일치 → INVALID_CREDENTIALS (어느 쪽인지 노출 안 함).
    /// Phase 3에서 BCrypt.Verify(password, account.password_hash)로 교체 예정.
    /// </summary>
    private const int MinLength = 4;
    private const int MaxLength = 16;

    private static async Task<AccountRow?> AuthenticateAsync(SessionComponent session, string username, string password)
    {
        // 기본 길이 검증 — DB 조회 전에 차단 (RegisterProcessor와 동일 기준)
        if (username.Length < MinLength || username.Length > MaxLength ||
            password.Length < MinLength || password.Length > MaxLength)
        {
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { ErrorCode = ErrorCode.InvalidCredentials }
            });
            return null;
        }

        AccountRow? account;
        try
        {
            account = await DatabaseSystem.Instance.Game.Accounts.SelectByUsernameAsync(username);
        }
        catch (Exception ex)
        {
            GameLogger.Error("Login", $"계정 조회 실패: {username}", ex);
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { ErrorCode = ErrorCode.DbError }
            });
            return null;
        }

        // Phase 3: account.password_hash 대신 BCrypt.Verify(password, account.password_hash) 사용
        if (account == null || account.password_hash != password)
        {
            GameLogger.Warn("Login", $"인증 실패: {username}");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { ErrorCode = ErrorCode.InvalidCredentials }
            });
            return null;
        }

        return account;
    }
}
