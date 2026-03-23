using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class ReqRoomEnter : IPacketPayload { }

[MessagePackObject]
public sealed class ResRoomEnter : IPacketPayload
{
    [Key(0)] public ErrorCode ErrorCode { get; set; }
}

[MessagePackObject]
public sealed class NotiRoomEnter : IPacketPayload
{
    [Key(0)] public ulong  PlayerId   { get; set; }
    [Key(1)] public string PlayerName { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class ReqRoomChat : IPacketPayload
{
    [Key(0)] public string Message { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class NotiRoomChat : IPacketPayload
{
    [Key(0)] public ulong  PlayerId   { get; set; }
    [Key(1)] public string PlayerName { get; set; } = string.Empty;
    [Key(2)] public string Message    { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class ReqRoomExit : IPacketPayload { }

[MessagePackObject]
public sealed class ResRoomExit : IPacketPayload { }

[MessagePackObject]
public sealed class NotiRoomExit : IPacketPayload
{
    [Key(0)] public ulong  PlayerId   { get; set; }
    [Key(1)] public string PlayerName { get; set; } = string.Empty;
}
