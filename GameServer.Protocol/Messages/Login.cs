using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class ReqLogin : IPacketPayload
{
    [Key(0)] public string Username { get; set; } = string.Empty;
    [Key(1)] public string Password { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class ResLogin : IPacketPayload
{
    [Key(0)] public ulong    PlayerId   { get; set; }
    [Key(1)] public string   PlayerName { get; set; } = string.Empty;
    [Key(2)] public ErrorCode ErrorCode { get; set; }
}
