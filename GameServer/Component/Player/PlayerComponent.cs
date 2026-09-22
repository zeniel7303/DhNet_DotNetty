using Common.Logging;
using Common.Server.Component;
using Common.Server.Routing;
using GameServer.Controllers;
using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;
using GameServer.World;

namespace GameServer.Component.Player;

public class PlayerComponent : BaseComponent
{
    public ulong AccountId { get; }
    public string Name { get; }
    public SessionComponent Session { get; }

    private int _disconnected;
    private readonly object _disposeLock = new();

    private IReadOnlyDictionary<Type, IRouter> _routeTable = new Dictionary<Type, IRouter>();

    // private set — OnDispose에서 lock 안에 null 처리를 위해 쓰기 가능
    public PlayerLobbyComponent     Lobby     { get; private set; }
    public PlayerRoomComponent      Room      { get; private set; }
    public PlayerCharacterComponent Character { get; private set; }
    public PlayerWorldComponent     World     { get; private set; }
    public PlayerSaveComponent      Save      { get; private set; }

    public PlayerComponent(SessionComponent session, string name, ulong accountId)
    {
        AccountId = accountId;
        Name = string.IsNullOrWhiteSpace(name) ? "TempUser" + AccountId : name;
        Session = session;
        World     = new PlayerWorldComponent();
        Character = new PlayerCharacterComponent(this);
        Lobby     = new PlayerLobbyComponent(this);
        Room      = new PlayerRoomComponent(this);
        Save      = new PlayerSaveComponent(this);

        // RegisterControllers()는 Initialize()에서 수행 — WorkerSystem.Add() 시 호출됨
    }

    public override void Initialize()
    {
        _routeTable = RegisterControllers();
        Session.PacketHandler = HandlePacket;
    }

    private IReadOnlyDictionary<Type, IRouter> RegisterControllers()
    {
        PlayerBaseController[] controllers =
        [
            new PlayerLobbyController(this),
            new PlayerRoomController(this),
            new PlayerRpgController(this),
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

        Save.Update(dt);

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

    public bool TryBeginDisconnectCleanup()
        => Interlocked.Exchange(ref _disconnected, 1) == 0;

    // 룸·로비에서 빼고 세션을 닫는다. 스냅샷 적재와 Flush는 호출하지 않는다.
    public void CleanupPresence()
    {
        try
        {
            PlayerRoomComponent? room;
            PlayerLobbyComponent? lobby;
            lock (_disposeLock)
            {
                room = Room;
                lobby = Lobby;
            }

            room?.Disconnect();
            lobby?.Disconnect();
            Session.DetachPlayer();
            Session.Dispose();
        }
        catch (Exception ex)
        {
            GameLogger.Error("PlayerComponent", $"연결 해제 정리 중 예외 (AccountId={AccountId}): {ex.Message}", ex);
        }
    }

    private async Task DisconnectAsync()
    {
        if (!TryBeginDisconnectCleanup()) return;

        // 존 입장이 끝난 계정은 Session 조율 경로로 보낸다. 여기서 Flush하지 않는다.
        if (ContentZone.Shared.IsSpawned(AccountId))
        {
            try
            {
                await SessionZoneDisconnect.CompleteAsync(this, alreadyOnPlayerWorker: true);
            }
            catch (Exception ex)
            {
                GameLogger.Error("PlayerComponent", $"존 끊김 조율 실패 (AccountId={AccountId}): {ex.Message}", ex);
                try
                {
                    PlayerSystem.Instance.Remove(this);
                }
                catch (Exception removeEx)
                {
                    GameLogger.Error("PlayerComponent", $"존 끊김 제거 실패 (AccountId={AccountId}): {removeEx.Message}", removeEx);
                }
            }
            return;
        }

        // 세션은 flush보다 먼저 닫는다. flush가 예외로 끝나도 아래에서 플레이어는 반드시 뺀다.
        // 전송이 끝나지 않은 flush는 await가 돌아오지 않으므로 Remove까지 가지 않는다.
        try
        {
            PlayerCharacterComponent? character = null;
            lock (_disposeLock)
            {
                character = Character;
            }

            CleanupPresence();

            // 성공, 명시적 실패, 예외 중 하나로 flush 시도가 끝난 뒤에만 제거한다.
            await Save.SaveAsync(character, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // _ = DisconnectAsync() fire-and-forget 경로에서 예외가 unhandled Task가 되지 않도록 잡는다.
            GameLogger.Error("PlayerComponent", $"DisconnectAsync flush 중 예외 (AccountId={AccountId}): {ex.Message}", ex);
        }
        finally
        {
            try
            {
                PlayerSystem.Instance.Remove(this);
            }
            catch (Exception ex)
            {
                GameLogger.Error("PlayerComponent", $"DisconnectAsync 제거 중 예외 (AccountId={AccountId}): {ex.Message}", ex);
            }
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
        lock (_disposeLock)
        {
            Lobby     = null!;
            Room      = null!;
            Character = null!;
            World     = null!;
            Save      = null!;
        }
    }
}
