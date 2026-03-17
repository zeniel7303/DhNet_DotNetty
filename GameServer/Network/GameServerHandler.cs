using Common.Logging;
using DotNetty.Transport.Channels;
using GameServer.Component.Player;
using GameServer.Controllers;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Network;

public class GameServerHandler : SimpleChannelInboundHandler<GamePacket>
{
    private SessionComponent? _session;

    public override void ChannelActive(IChannelHandlerContext ctx)
    {
        _session = new SessionComponent(ctx.Channel);
        SessionSystem.Instance.EnqueueAdd(_session);
        GameLogger.Info("연결", $"{ctx.Channel.RemoteAddress}");
    }

    public override void ChannelInactive(IChannelHandlerContext ctx)
    {
        if (_session != null)
        {
            // SessionSystem이 Disconnect 처리 전담 — IsEntryHandshakeCompleted에 따라 경로 분기
            SessionSystem.Instance.EnqueueDisconnect(_session);
        }
        GameLogger.Info("해제", $"{ctx.Channel.RemoteAddress}");
        _session = null;
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
    {
        if (_session == null) return;

        if (packet.PayloadCase == GamePacket.PayloadOneofCase.ReqLogin)
        {
            _ = HandleAsync(_session, packet.ReqLogin);
        }
        else
        {
            // 로그인 후 패킷은 세션 큐에 적재 — PlayerComponent 워커가 틱마다 드레인
            _session.EnqueuePacket(packet);
        }
    }

    public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
    {
        GameLogger.Error("예외", ex.Message, ex);
        _ = ctx.CloseAsync();
    }
    
    private static async Task HandleAsync(SessionComponent session, ReqLogin req)
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

        // 로비 자동 배정 — EnqueueEvent로 PlayerComponent 워커 스레드에서 실행
        // → TryEnter 내부의 CurrentLobby 쓰기가 워커 스레드에서 일어나므로 volatile 불필요
        var defaultLobby = LobbySystem.Instance.GetDefaultLobby();
        if (defaultLobby == null)
        {
            // 모든 로비 만원 — 플레이어가 PlayerSystem에 등록된 채 좀비 상태가 되지 않도록 즉시 종료
            GameLogger.Warn("Login", $"로비 자동 배정 불가 — 모든 로비 만원: {player.Name}");
            await session.SendAsync(new GamePacket
            {
                ResLogin = new ResLogin { PlayerId = 0, PlayerName = string.Empty }
            });
            player.DisconnectForNextTick();
            return;
        }

        player.EnqueueEvent(() =>
        {
            if (defaultLobby.TryEnter(player)) return;

            // GetDefaultLobby 체크 이후 로비가 꽉 찬 경우 — 다른 로비 재시도
            var fallback = LobbySystem.Instance.GetDefaultLobby();
            if (fallback != null && fallback.TryEnter(player)) return;

            // 모든 로비 만원 — ResLogin은 이미 전송됐으므로 강제 종료
            GameLogger.Warn("Login", $"로비 TryEnter 실패 (경쟁 조건) — 강제 종료: {player.Name}");
            player.DisconnectForNextTick();
        });

        GameLogger.Info("Login", $"로그인 성공: {player.Name} (Id={player.PlayerId})");
        
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
