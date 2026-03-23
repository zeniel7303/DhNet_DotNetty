using System.Collections.Concurrent;
using Common.Logging;
using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;

namespace GameServer.Component.Room;

public class RoomComponent : BaseComponent
{
    public ulong RoomId { get; }
    public ulong LobbyId { get; }
    public string Name => $"Room {RoomId}";
    public int PlayerCount => Math.Max(0, _state);
    public int Capacity => MaxPlayers;

    private const int MaxPlayers = 2;

    private readonly ConcurrentDictionary<ulong, PlayerComponent> _players = new();

    // 방이 비었을 때 호출 — LobbyComponent.RemoveRoom() 주입
    private readonly Action _onEmpty;
    // 룸 퇴장 후 로비 복귀 — LobbyComponent.TryEnter() 주입
    private readonly Func<PlayerComponent, bool> _returnToLobby;

    // _reservedCount + _closing을 단일 int로 통합.
    // -1 = 닫히는 중, 0~MaxPlayers = 예약된 슬롯 수.
    private int _state = 0;

    // CAS 패턴 — lock 없이 원자적으로 슬롯 예약 (LobbyComponent._roomLock 내부에서만 호출)
    internal bool TryReserve()
    {
        int current;
        do
        {
            current = _state;
            if (current < 0 || current >= MaxPlayers) return false;
        } while (Interlocked.CompareExchange(ref _state, current + 1, current) != current);
        return true;
    }

    private bool TryReleaseAndClose()
    {
        int current;
        do
        {
            current = _state;
            if (current <= 0) return false;
            int next = current == 1 ? -1 : current - 1;
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                return next == -1;
            }
        } while (true);
    }

    public RoomComponent(ulong roomId, ulong lobbyId, Action onEmpty, Func<PlayerComponent, bool> returnToLobby)
    {
        RoomId = roomId;
        LobbyId = lobbyId;
        _onEmpty = onEmpty;
        _returnToLobby = returnToLobby;
    }

    public override void Initialize() { }

    public void Enter(PlayerComponent player)
    {
        if (!player.Session.IsConnected)
        {
            if (TryReleaseAndClose()) _onEmpty();
            GameLogger.Warn($"Room:{RoomId}", $"입장 취소 (연결 해제): {player.Name}");
            return;
        }

        if (!_players.TryAdd(player.AccountId, player))
        {
            if (TryReleaseAndClose()) _onEmpty();
            GameLogger.Warn($"Room:{RoomId}", $"입장 거부 (중복): {player.Name}");
            return;
        }

        player.Room.CurrentRoom = this;

        GameLogger.Info($"Room:{RoomId}", $"입장: {player.Name} ({_players.Count}/{MaxPlayers})");

        DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
        {
            account_id = player.AccountId,
            room_id    = RoomId,
            action     = "enter",
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");

        _ = player.Session.SendAsync(GamePacket.From(new ResRoomEnter { ErrorCode = ErrorCode.Success }));

        var noti = GamePacket.From(new NotiRoomEnter { PlayerId = player.AccountId, PlayerName = player.Name });

        foreach (var (id, p) in _players)
        {
            if (id != player.AccountId) _ = p.Session.SendAsync(noti);
        }
    }

    public void Leave(PlayerComponent player, bool isDisconnect)
    {
        if (!_players.TryRemove(player.AccountId, out _)) return;

        var shouldClose = TryReleaseAndClose();

        player.Room.CurrentRoom = null;

        GameLogger.Info($"Room:{RoomId}", $"퇴장: {player.Name} (disconnect={isDisconnect}, 잔여 {_players.Count}명)");

        DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
        {
            account_id = player.AccountId,
            room_id    = RoomId,
            action     = isDisconnect ? "disconnect" : "exit",
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");

        if (!isDisconnect)
        {
            ReturnToLobby(player);
            _ = player.Session.SendAsync(GamePacket.From(new ResRoomExit()));
        }

        var noti = GamePacket.From(new NotiRoomExit { PlayerId = player.AccountId, PlayerName = player.Name });

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        if (shouldClose) _onEmpty();
    }

    public void Chat(PlayerComponent sender, string message)
    {
        var noti = GamePacket.From(new NotiRoomChat
        {
            PlayerId   = sender.AccountId,
            PlayerName = sender.Name,
            Message    = message
        });

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }
    }

    public IReadOnlyList<(ulong AccountId, string Name)> GetPlayerList()
        => _players.Values.Select(p => (p.AccountId, p.Name)).ToList();

    public bool Broadcast(string message)
    {
        if (IsDisposed) return false;

        var noti = GamePacket.From(new NotiRoomChat { PlayerId = 0, PlayerName = "System", Message = message });

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        return true;
    }

    private void ReturnToLobby(PlayerComponent player)
    {
        if (_returnToLobby(player)) return;

        GameLogger.Warn($"Room:{RoomId}", $"로비 복귀 실패 — 연결 종료 (LobbyId={LobbyId}): {player.Name}");
        player.DisconnectForNextTick();
    }

    protected override void OnDispose()
    {
        foreach (var p in _players.Values)
        {
            p.DisconnectForNextTick();
        }

        _players.Clear();
    }
}
