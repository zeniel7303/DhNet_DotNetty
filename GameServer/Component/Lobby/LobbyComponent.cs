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
    // Interlocked.CompareExchange가 이미 메모리 배리어를 포함하므로 volatile 불필요
    private int _playerCount;
    public int PlayerCount => _playerCount;
    public bool IsFull => _playerCount >= MaxCapacity;

    private readonly ConcurrentDictionary<ulong, PlayerComponent> _players = new();
    // _rooms 레지스트리를 소유하는 쪽이 RoomComponent의 생명주기(Dispose)도 책임진다.
    // RoomSystem은 틱 스레드 제공자일 뿐 — Dispose 호출은 LobbyComponent가 직접 수행한다.
    private readonly Dictionary<ulong, RoomComponent> _rooms = new();
    private readonly object _roomLock = new();

    private readonly int _maxPlayersPerRoom;

    public LobbyComponent(ulong lobbyId, int maxCapacity, int maxPlayersPerRoom)
    {
        LobbyId = lobbyId;
        MaxCapacity = maxCapacity;
        _maxPlayersPerRoom = maxPlayersPerRoom;
    }

    public override void Initialize() { }

    // CAS로 정원 예약 후 TryAdd. 반환 false = 정원 초과 또는 중복 입장.
    // PlayerComponent 워커 스레드(EnqueueEvent 경유) 또는 LoginController 스레드에서 호출
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

        // LoginController → EnqueueEvent → 워커 틱에서 호출되므로 PlayerComponent 워커 스레드
        player.Lobby.CurrentLobby = this;
        GameLogger.Info($"Lobby:{LobbyId}", $"입장: {player.Name} (현재 {_players.Count}명)");
        return true;
    }

    public void Leave(PlayerComponent player)
    {
        if (!_players.TryRemove(player.AccountId, out _))
        {
            // 룸 입장 중 disconnect 시 이미 로비에서 퇴장한 상태일 수 있음 — 정상 경로
            GameLogger.Info($"Lobby:{LobbyId}", $"퇴장 요청: {player.Name} — 목록에 없음 (무시됨)");
            return;
        }

        Interlocked.Decrement(ref _playerCount);
        player.Lobby.CurrentLobby = null;
        GameLogger.Info($"Lobby:{LobbyId}", $"퇴장: {player.Name} (현재 {_players.Count}명)");
    }

    public bool Broadcast(string message)
    {
        if (IsDisposed) return false;

        var noti = new GamePacket
        {
            NotiSystem = new NotiSystem { Message = message }
        };

        foreach (var p in _players.Values)
        {
            _ = p.Session.SendAsync(noti);
        }

        return true;
    }

    // 신규 룸 생성 + 첫 슬롯 예약. 실패 시 null (불가능한 경로이나 방어적 처리).
    public RoomComponent? CreateRoom()
    {
        RoomComponent newRoom;
        lock (_roomLock)
        {
            var roomId = IdGenerators.Room.Next();
            newRoom = new RoomComponent(roomId, LobbyId, _maxPlayersPerRoom,
                onEmpty: () => RemoveRoom(roomId),
                returnToLobby: p => TryEnter(p));
            _rooms.TryAdd(newRoom.RoomId, newRoom);

            if (!newRoom.TryReserve())
            {
                _rooms.Remove(newRoom.RoomId);
                GameLogger.Warn($"Lobby:{LobbyId}", $"CreateRoom 첫 슬롯 예약 실패: RoomId={newRoom.RoomId}");
                return null;
            }

            GameLogger.Info($"Lobby:{LobbyId}", $"Room 생성: RoomId={newRoom.RoomId}");
        }

        RoomSystem.Instance.Add(newRoom);  // Initialize() 자동 호출 — lock 외부
        return newRoom;
    }

    // 현재 로비의 룸 목록 반환 (게임 시작 여부 포함)
    public RoomInfo[] GetRoomList()
    {
        lock (_roomLock)
            return _rooms.Values.Select(r => new RoomInfo
            {
                RoomId      = r.RoomId,
                PlayerCount = r.PlayerCount,
                MaxPlayers  = r.Capacity,
                IsStarted   = r.IsGameStarted
            }).ToArray();
    }

    public RoomComponent? TryGetRoom(ulong roomId)
    {
        lock (_roomLock)
            return _rooms.GetValueOrDefault(roomId);
    }

    public void RemoveRoom(ulong roomId)
    {
        RoomComponent? room;
        lock (_roomLock)
        {
            if (!_rooms.Remove(roomId, out room)) return;
        }

        RoomSystem.Instance.Remove(room);  // 워커에서 제거
        room.Dispose();                    // LobbyComponent가 Dispose 책임자
        GameLogger.Info($"Lobby:{LobbyId}", $"Room 제거: RoomId={roomId}");
    }

    public int RoomCount { get { lock (_roomLock) return _rooms.Count; } }

    public IReadOnlyList<RoomComponent> GetRooms()
    {
        lock (_roomLock)
            return _rooms.Values.ToList();
    }

    protected override void OnDispose()
    {
        // 잔류 플레이어 강제 종료 — 서버 종료 등 비정상 경로에서만 실행됨
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
            RoomSystem.Instance.Remove(room);  // 워커에서 제거
            room.Dispose();                    // LobbyComponent가 Dispose 책임자
        }
    }
}
