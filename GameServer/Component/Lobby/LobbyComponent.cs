using System.Collections.Concurrent;
using Common.Logging;
using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Component.Room;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Lobby;

public class LobbyComponent : BaseComponent
{
    public ulong LobbyId { get; }
    public int MaxCapacity { get; }

    // CAS 기반 정원 추적 — 여러 PlayerComponent 워커에서 원자적으로 예약/해제
    private int _playerCount;
    public int PlayerCount => _playerCount;
    public bool IsFull => _playerCount >= MaxCapacity;

    private readonly ConcurrentDictionary<ulong, PlayerComponent> _players = new();
    private readonly ConcurrentDictionary<ulong, RoomComponent> _rooms = new();
    private readonly object _roomLock = new();

    public LobbyComponent(ulong lobbyId, int maxCapacity)
    {
        LobbyId = lobbyId;
        MaxCapacity = maxCapacity;
    }

    public override void Initialize() { }

    // CAS로 정원 예약 후 TryAdd. 반환 false = 정원 초과 또는 중복 입장.
    public bool TryEnter(PlayerComponent player)
    {
        int current;
        do
        {
            current = _playerCount;
            if (current >= MaxCapacity) return false;
        } while (Interlocked.CompareExchange(ref _playerCount, current + 1, current) != current);

        if (!_players.TryAdd(player.AccountId, player))
        {
            Interlocked.Decrement(ref _playerCount);
            GameLogger.Warn($"Lobby:{LobbyId}", $"중복 입장 거부, 슬롯 반환: {player.Name}");
            return false;
        }

        player.Lobby.CurrentLobby = this;
        GameLogger.Info($"Lobby:{LobbyId}", $"입장: {player.Name} (현재 {_players.Count}명)");
        return true;
    }

    public void Leave(PlayerComponent player)
    {
        if (!_players.TryRemove(player.AccountId, out _))
        {
            GameLogger.Info($"Lobby:{LobbyId}", $"퇴장 요청: {player.Name} — 목록에 없음 (무시됨)");
            return;
        }

        Interlocked.Decrement(ref _playerCount);
        player.Lobby.CurrentLobby = null;
        GameLogger.Info($"Lobby:{LobbyId}", $"퇴장: {player.Name} (현재 {_players.Count}명)");
    }

    public void Chat(PlayerComponent sender, string message)
    {
        var noti = GamePacket.From(new NotiLobbyChat
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

    public bool Broadcast(string message)
    {
        if (IsDisposed) return false;

        var noti = GamePacket.From(new NotiLobbyChat { PlayerId = 0, PlayerName = "System", Message = message });

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        return true;
    }

    // 빈 방 탐색 + 없으면 신규 생성. lock으로 TOCTOU 방지 (순회+생성+추가 원자성).
    public RoomComponent GetOrCreateRoom()
    {
        lock (_roomLock)
        {
            foreach (var room in _rooms.Values)
            {
                if (room.TryReserve()) return room;
            }

            var roomId = IdGenerators.Room.Next();
            var newRoom = new RoomComponent(roomId, LobbyId,
                onEmpty: () => RemoveRoom(roomId),
                returnToLobby: p => TryEnter(p));
            newRoom.Initialize();

            _rooms.TryAdd(newRoom.RoomId, newRoom);

            if (!newRoom.TryReserve())
            {
                GameLogger.Warn($"Lobby:{LobbyId}", $"신규 Room 첫 슬롯 예약 실패 (불가능한 경로): RoomId={newRoom.RoomId}");
            }

            GameLogger.Info($"Lobby:{LobbyId}", $"신규 Room 생성: RoomId={newRoom.RoomId}");
            return newRoom;
        }
    }

    public RoomComponent? TryGetRoom(ulong roomId) => _rooms.GetValueOrDefault(roomId);

    public void RemoveRoom(ulong roomId)
    {
        RoomComponent? room;
        lock (_roomLock)
        {
            if (!_rooms.TryRemove(roomId, out room)) return;
        }

        room.Dispose();
        GameLogger.Info($"Lobby:{LobbyId}", $"Room 제거: RoomId={roomId}");
    }

    public int RoomCount => _rooms.Count;

    public IReadOnlyList<RoomComponent> GetRooms() => _rooms.Values.ToList();

    protected override void OnDispose()
    {
        foreach (var p in _players.Values)
        {
            p.DisconnectForNextTick();
        }

        _players.Clear();

        List<RoomComponent> rooms;
        lock (_roomLock)
        {
            rooms = _rooms.Values.ToList();
            _rooms.Clear();
        }

        foreach (var room in rooms)
        {
            room.Dispose();
        }
    }
}
