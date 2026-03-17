using Common.Logging;
using GameServer.Component.Lobby;
using GameServer.Component.Room;
using GameServer.Protocol;

namespace GameServer.Systems;

public class LobbySystem
{
    public static readonly LobbySystem Instance = new();

    private LobbyComponent[] _lobbies = Array.Empty<LobbyComponent>();

    public void Initialize(int lobbyCount = 1, int lobbyCapacity = 100)
    {
        _lobbies = Enumerable.Range(0, lobbyCount)
            .Select(_ => new LobbyComponent(IdGenerators.Lobby.Next(), lobbyCapacity))
            .ToArray();

        foreach (var lobby in _lobbies)
            lobby.Initialize();

        GameLogger.Info("LobbySystem", $"로비 {lobbyCount}개 생성 완료 (capacity={lobbyCapacity})");
    }

    // 가득 차지 않은 로비 중 현재 인원이 가장 많은 로비 반환 — 소규모 접속 시 클러스터링
    public LobbyComponent? GetDefaultLobby()
        => _lobbies.Where(l => !l.IsFull).MaxBy(l => l.PlayerCount);

    public LobbyComponent? TryGetLobby(ulong lobbyId)
        => Array.Find(_lobbies, l => l.LobbyId == lobbyId);

    public LobbyInfo[] GetLobbyList()
        => _lobbies.Select(l => new LobbyInfo
        {
            LobbyId     = l.LobbyId,
            PlayerCount = l.PlayerCount,
            MaxCapacity = l.MaxCapacity,
            IsFull      = l.IsFull
        }).ToArray();

    public IReadOnlyList<RoomComponent> GetAllRooms()
        => _lobbies.SelectMany(l => l.GetRooms()).ToList();

    public RoomComponent? TryGetRoom(ulong roomId)
    {
        foreach (var lobby in _lobbies)
        {
            var room = lobby.TryGetRoom(roomId);
            if (room != null) return room;
        }
        return null;
    }

    public int GetTotalPlayerCount()
        => _lobbies.Sum(l => l.PlayerCount);
}
