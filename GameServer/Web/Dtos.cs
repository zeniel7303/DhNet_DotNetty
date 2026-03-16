namespace GameServer.Web;

public record HealthDto(string Status);
public record LobbyDto(int PlayerCount);
public record RoomDto(ulong Id, string Name, int PlayerCount, int Capacity);
public record BroadcastBody(string Message);
