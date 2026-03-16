using System.Collections.Concurrent;
using Common.Logging;
using GameServer.Entities;

namespace GameServer.Systems;

public class LobbySystem
{
    public static readonly LobbySystem Instance = new();

    public Lobby Lobby { get; } = new();

    private readonly ConcurrentDictionary<ulong, Room> _rooms = new();
    private readonly object _roomLock = new();

    public Room GetOrCreateRoom()
    {
        // lock으로 "빈 방 탐색 + 신규 방 생성"을 원자적으로 처리 → TOCTOU 방지
        lock (_roomLock)
        {
            foreach (var room in _rooms.Values)
            {
                if (room.TryReserve())
                {
                    return room;
                }
            }

            var newRoom = new Room(IdGenerators.Room.Next());
            _rooms.TryAdd(newRoom.RoomId, newRoom);
            newRoom.TryReserve();
            GameLogger.Info("LobbySystem", $"신규 Room 생성: RoomId={newRoom.RoomId}");
            return newRoom;
        }
    }

    public IReadOnlyList<Room> GetRooms() => _rooms.Values.ToList();

    public Room? TryGetRoom(ulong roomId) =>
        _rooms.TryGetValue(roomId, out var room) ? room : null;

    public void RemoveRoom(ulong roomId)
    {
        lock (_roomLock)
        {
            if (_rooms.TryRemove(roomId, out var room))
            {
                room.Close();
                GameLogger.Info("LobbySystem", $"Room 제거: RoomId={roomId}");
            }
        }
    }
}
