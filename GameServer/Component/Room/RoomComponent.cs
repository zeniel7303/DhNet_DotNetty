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
    // 마지막 슬롯 반환(1→-1)이 단일 CAS로 처리되므로 TryReserve와의 레이스가 완전히 제거됨.
    private int _state = 0;

    // CAS 패턴 — lock 없이 원자적으로 슬롯 예약 (LobbyComponent._roomLock 내부에서만 호출)
    internal bool TryReserve()
    {
        int current;
        do
        {
            current = _state;
            // -1 = 닫히는 중, MaxPlayers = 정원 초과
            if (current < 0 || current >= MaxPlayers) return false;
        } while (Interlocked.CompareExchange(ref _state, current + 1, current) != current);
        return true;
    }

    // 슬롯을 반환하고, 마지막 슬롯이면 _state를 원자적으로 -1(closing)로 전환.
    // 반환값 true = 이 스레드가 방 종료 책임을 가짐 → _onEmpty() 호출 필요.
    private bool TryReleaseAndClose()
    {
        int current;
        do
        {
            current = _state;
            if (current <= 0) return false;
            // 마지막 슬롯(1→-1): TryReserve의 CAS가 -1을 보고 즉시 실패하므로 레이스 없음
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

        if (!_players.TryAdd(player.PlayerId, player))
        {
            if (TryReleaseAndClose()) _onEmpty();
            GameLogger.Warn($"Room:{RoomId}", $"입장 거부 (중복): {player.Name}");
            return;
        }

        // PlayerLobbyComponent.RoomEnter → PlayerComponent 워커 스레드에서 호출
        player.Room.CurrentRoom = this;

        GameLogger.Info($"Room:{RoomId}", $"입장: {player.Name} ({_players.Count}/{MaxPlayers})");

        DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
        {
            player_id  = player.PlayerId,
            room_id    = RoomId,
            action     = "enter",
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");

        _ = player.Session.SendAsync(new GamePacket
            { ResRoomEnter = new ResRoomEnter { Success = true } });

        var noti = new GamePacket
        {
            NotiRoomEnter = new NotiRoomEnter { PlayerId = player.PlayerId, PlayerName = player.Name }
        };

        foreach (var (id, p) in _players)
        {
            if (id != player.PlayerId) _ = p.Session.SendAsync(noti);
        }
    }

    public void Leave(PlayerComponent player, bool isDisconnect)
    {
        if (!_players.TryRemove(player.PlayerId, out _)) return;

        var shouldClose = TryReleaseAndClose();

        // PlayerRoomComponent.Exit/Disconnect → PlayerComponent 워커 스레드에서 호출
        player.Room.CurrentRoom = null;

        GameLogger.Info($"Room:{RoomId}", $"퇴장: {player.Name} (disconnect={isDisconnect}, 잔여 {_players.Count}명)");

        DatabaseSystem.Instance.GameLog.RoomLogs.InsertAsync(new RoomLogRow
        {
            player_id  = player.PlayerId,
            room_id    = RoomId,
            action     = isDisconnect ? "disconnect" : "exit",
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");

        if (!isDisconnect)
        {
            ReturnToLobby(player);
            _ = player.Session.SendAsync(new GamePacket { ResRoomExit = new ResRoomExit() });
        }

        var noti = new GamePacket
        {
            NotiRoomExit = new NotiRoomExit { PlayerId = player.PlayerId, PlayerName = player.Name }
        };

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        if (shouldClose) _onEmpty();
    }

    public void Chat(PlayerComponent sender, string message)
    {
        GameLogger.Info($"Room:{RoomId}", $"채팅: {sender.Name}: {message}");

        DatabaseSystem.Instance.GameLog.ChatLogs.InsertAsync(new ChatLogRow
        {
            player_id  = sender.PlayerId,
            room_id    = RoomId,
            channel    = "room",
            message    = message,
            created_at = DateTime.UtcNow
        }).FireAndForget("Room");

        var noti = new GamePacket
        {
            NotiRoomChat = new NotiRoomChat
            {
                PlayerId   = sender.PlayerId,
                PlayerName = sender.Name,
                Message    = message
            }
        };

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }
    }

    public bool Broadcast(string message)
    {
        if (IsDisposed) return false;

        var noti = new GamePacket
        {
            NotiRoomChat = new NotiRoomChat { PlayerId = 0, PlayerName = "System", Message = message }
        };

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        return true;
    }

    // 룸 퇴장 후 원래 로비로 복귀. 복귀 실패 시 플레이어 강제 종료.
    private void ReturnToLobby(PlayerComponent player)
    {
        if (_returnToLobby(player)) return;

        GameLogger.Warn($"Room:{RoomId}", $"로비 복귀 실패 — 연결 종료 (LobbyId={LobbyId}): {player.Name}");
        // 로비 복귀 실패 시 WorkerSystem 등록 상태이므로 큐에 넣어 워커 틱에서 처리
        player.DisconnectForNextTick();
    }

    protected override void OnDispose()
    {
        // 잔류 플레이어 강제 종료 — 서버 종료 등 비정상 경로에서만 실행됨
        foreach (var p in _players.Values)
        {
            p.DisconnectForNextTick();
        }

        _players.Clear();
    }
}
