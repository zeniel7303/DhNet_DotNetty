using Common.Logging;
using Common.Server.Component;
using Common.Server.Routing;
using GameServer.Controllers;
using GameServer.Database;
using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Player;

public class PlayerComponent : BaseComponent
{
    public ulong AccountId { get; }
    public string Name { get; }
    public SessionComponent Session { get; }

    private int _disconnected;

    // DB InsertAsync 완료 여부 — DisconnectAsync에서 UpdateLogoutAsync 실행 조건
    // InsertAsync 이전에 Dispose되는 경우(세션 소실 등) UpdateLogout 실행 시 DB 오류 방지
    private int _dbInserted;

    private IReadOnlyDictionary<Type, IRouter> _routeTable = new Dictionary<Type, IRouter>();

    // private set — OnDispose에서 lock 안에 null 처리를 위해 쓰기 가능
    public PlayerLobbyComponent Lobby { get; private set; }
    public PlayerRoomComponent Room { get; private set; }

    public PlayerComponent(SessionComponent session, string name, ulong accountId)
    {
        AccountId = accountId;
        Name = string.IsNullOrWhiteSpace(name) ? "TempUser" + AccountId : name;
        Session = session;
        Lobby = new PlayerLobbyComponent(this);
        Room = new PlayerRoomComponent(this);

        // RegisterControllers()는 Initialize()에서 수행 — WorkerSystem.Add() 시 호출됨
    }

    // LoginController에서 DB InsertAsync 완료 후 호출 — 이후 DisconnectAsync의 UpdateLogout 활성화
    public void MarkDbInserted() => Interlocked.Exchange(ref _dbInserted, 1);

    public override void Initialize()
    {
        _routeTable = RegisterControllers();
        Lobby.Initialize();
        Room.Initialize();
        Session.PacketHandler = HandlePacket;
    }

    private IReadOnlyDictionary<Type, IRouter> RegisterControllers()
    {
        PlayerBaseController[] controllers =
        [
            new PlayerLobbyController(this),
            new PlayerRoomController(this),
            new PlayerHeartbeatController(this),
        ];

        var table = new Dictionary<Type, IRouter>();
        foreach (var controller in controllers)
        {
            foreach (var router in controller.Routes())
            {
                var reqType = router.GetRequestType();
                if (table.ContainsKey(reqType))
                {
                    throw new InvalidOperationException($"[PlayerComponent] 중복 라우터: {reqType.Name}");
                }
                table[reqType] = router;
            }
        }
        return table;
    }

    public override void Update(float dt)
    {
        base.Update(dt);

        // DisconnectForNextTick → base.Update에서 _ = DisconnectAsync() 시작 후
        // IsDisposed가 true이면 이미 Dispose된 상태이므로 패킷 처리 스킵
        if (IsDisposed) return;

        // 큐 드레인은 SessionComponent 소유 — 라우팅만 HandlePacket 콜백으로 위임
        Session.DrainPackets();
    }

    // SessionComponent.PacketHandler로 등록 — 드레인된 패킷 1개씩 수신하여 라우팅
    // WorkerSystem: 동일 PlayerComponent는 항상 동일 워커에 고정 → 단일 스레드 직렬 실행 보장
    private void HandlePacket(GamePacket packet)
    {
        try
        {
            var (type, payload) = packet.ExtractPayload();
            if (type == null || payload == null)
            {
                GameLogger.Warn("PlayerComponent", $"미처리 패킷: {packet.PayloadCase}");
                return;
            }

            if (!_routeTable.TryGetValue(type, out var router))
            {
                GameLogger.Warn("PlayerComponent", $"라우터 없음: {type.Name}");
                return;
            }

            router.Handle(payload, response =>
            {
                if (response is GamePacket p)
                    _ = Session.SendAsync(p);
            });
        }
        catch (Exception ex)
        {
            GameLogger.Error("PlayerComponent", $"패킷 처리 오류: {packet.PayloadCase}", ex);
        }
    }

    private async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;

        try
        {
            // lock(this): Room/Lobby 참조 캡처 원자화
            // Disconnect ↔ Dispose 동시 진입 시 참조 교환 안전하게 수행
            // 실제 Disconnect 호출은 lock 외부 — 외부 컴포넌트 호출로 인한 교차 락 방지
            PlayerRoomComponent? room;
            PlayerLobbyComponent? lobby;
            lock (this)
            {
                room  = Room;
                lobby = Lobby;
            }

            room?.Disconnect();
            lobby?.Disconnect();

            Session.DetachPlayer();
            Session.Dispose();

            // DB InsertAsync가 완료된 경우에만 UpdateLogout 실행
            // InsertAsync 이전 Dispose(ImmediateFinalize, 세션 소실 등) 시 DB 오염 방지
            // Volatile.Read: ImmediateFinalize 경로(ThreadPool)에서 최신 값 보장
            if (Volatile.Read(ref _dbInserted) == 1)
            {
                var logoutAt = DateTime.UtcNow;
                try
                {
                    await DatabaseSystem.Instance.Game.Players.UpdateLogoutAsync(AccountId, logoutAt);
                }
                catch (Exception ex)
                {
                    GameLogger.Error("PlayerComponent", $"플레이어 로그아웃 DB 저장 실패: {AccountId}", ex);
                }

                DatabaseSystem.Instance.GameLog.LoginLogs.UpdateLogoutAsync(AccountId, logoutAt).FireAndForget("PlayerComponent");
            }

            // DB write 완료 후 Remove — PlayerSystem.WaitUntilEmptyAsync가 DB 동기화 완료를 정확히 감지하도록 보장
            PlayerSystem.Instance.Remove(this);
        }
        catch (Exception ex)
        {
            // _ = DisconnectAsync() fire-and-forget 경로에서 예외가 unhandled Task가 되지 않도록 최상위 catch.
            GameLogger.Error("PlayerComponent", $"DisconnectAsync 중 예외 (AccountId={AccountId}): {ex.Message}", ex);
        }
    }

    // SessionSystem의 InternalDisconnectSession에서 호출 — IsEntryHandshakeCompleted == true 경로
    // WorkerSystem 워커 틱에서 DisconnectAsync를 실행하여 DB I/O가 I/O EventLoop를 블로킹하지 않음
    public void DisconnectForNextTick()
        => EnqueueEvent(() => _ = DisconnectAsync());

    // SessionSystem의 InternalDisconnectSession에서 호출 — IsEntryHandshakeCompleted == false 경로
    // PlayerGameEnter 완료 전 연결 해제 시 즉시 정리 (WorkerSystem 미등록 상태)
    public void ImmediateFinalize()
        => _ = DisconnectAsync();

    // PlayerSystem.Remove → player.Dispose()에 의해 호출됨
    // DisconnectAsync가 항상 선행 실행되므로 참조 null 처리만 수행
    protected override void OnDispose()
    {
        lock (this)
        {
            Lobby = null!;
            Room  = null!;
        }
    }
}
