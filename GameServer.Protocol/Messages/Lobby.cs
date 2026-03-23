using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class ReqLobbyChat : IPacketPayload
{
    [Key(0)] public string Message { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class NotiLobbyChat : IPacketPayload
{
    [Key(0)] public ulong  PlayerId   { get; set; }
    [Key(1)] public string PlayerName { get; set; } = string.Empty;
    [Key(2)] public string Message    { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class ReqLobbyList : IPacketPayload { }

[MessagePackObject]
public sealed class LobbyInfo
{
    [Key(0)] public ulong LobbyId     { get; set; }
    [Key(1)] public int   PlayerCount { get; set; }
    [Key(2)] public int   MaxCapacity { get; set; }
    [Key(3)] public bool  IsFull      { get; set; }
}

[MessagePackObject]
public sealed class ResLobbyList : IPacketPayload
{
    [Key(0)] public List<LobbyInfo> Lobbies { get; set; } = new();
}
