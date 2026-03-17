using Common.Logging;
using Common.Server.Routing;
using GameServer.Controllers;
using GameServer.Database;
using GameServer.Network;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Entities;

public class Player
{
    public ulong Id { get; }
    public string Name { get; }
    public SessionComponent Session { get; }
    public Room? CurrentRoom { get; set; }

    private int _disconnected = 0;
    private readonly Dictionary<Type, IRouter> _routeTable;

    public Player(SessionComponent session, string name = "")
    {
        Id = IdGenerators.Player.Next();
        Name = string.IsNullOrEmpty(name) ? "TempUser" + Id : name;
        Session = session;
        _routeTable = BuildRouteTable();
    }

    private Dictionary<Type, IRouter> BuildRouteTable()
    {
        var routers = new RouterBuilder()
            .With<ReqLobbyChat>(req => LobbyController.HandleChat(Session, req))
            .With<ReqRoomEnter>(req => LobbyController.HandleRoomEnter(Session, req))
            .With<ReqRoomChat>(req => RoomController.HandleChat(Session, req))
            .With<ReqRoomExit>(req => RoomController.HandleExit(Session, req))
            .With<ReqHeartbeat>(_ => new GamePacket { ResHeartbeat = new ResHeartbeat() })
            .Build();

        return routers.ToDictionary(r => r.GetRequestType());
    }

    public void Dispatch(GamePacket packet)
    {
        var (type, payload) = packet.ExtractPayload();
        
        if (type == null || payload == null) return;
        
        if (_routeTable.TryGetValue(type, out var router))
        {
            router.Handle(payload, response =>
            {
                if (response != null) 
                    _ = Session.SendAsync(response);
            });
        }
    }

    // 워커 틱에서 처리 (핸드셰이크 완료 후 Disconnect) — Stage 4에서 EnqueueEvent로 교체 예정
    public void DisconnectForNextTick() => _ = DisconnectAsync();

    // 즉시 정리 (핸드셰이크 전 Disconnect, WorkerSystem 미등록 경로)
    public void ImmediateFinalize() => _ = DisconnectAsync();

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
        Session.DetachPlayer();
        Session.Dispose();

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
