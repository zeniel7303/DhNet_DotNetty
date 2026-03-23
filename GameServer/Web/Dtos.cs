namespace GameServer.Web;

public record HealthDto(string Status);
public record RoomDto(ulong Id, string Name, int PlayerCount, int Capacity);
public record BroadcastBody(string Message);

// Players
public record PlayerDto(ulong AccountId, string Name, ulong? LobbyId, ulong? RoomId);

// Lobbies
public record LobbyDetailDto(ulong LobbyId, int PlayerCount, int MaxCapacity, bool IsFull);

// Room detail
public record PlayerInRoomDto(ulong AccountId, string Name);
public record RoomDetailDto(ulong RoomId, ulong LobbyId, string Name, int PlayerCount, int Capacity, PlayerInRoomDto[] Players);

// Stats
public record StatsDto(int OnlinePlayers, int MaxPlayers, int ActiveRooms, int TotalLobbies, LobbyDetailDto[] Lobbies);
public record StatHistoryItemDto(int PlayerCount, DateTime CreatedAt);

// Server info
public record ServerInfoDto(string Version, string Uptime, int GamePort, int WebPort, int MaxPlayers);

// Analytics
public record ChatLogDto(ulong AccountId, ulong? RoomId, string Channel, string Message, DateTime CreatedAt);
public record LoginLogDto(ulong AccountId, string PlayerName, string? IpAddress, DateTime LoginAt, DateTime? LogoutAt);
public record RoomLogEntryDto(ulong AccountId, ulong RoomId, string Action, DateTime CreatedAt);
