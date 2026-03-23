using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class ReqRegister : IPacketPayload
{
    [Key(0)] public string Username { get; set; } = string.Empty;
    [Key(1)] public string Password { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class ResRegister : IPacketPayload
{
    [Key(0)] public ulong     AccountId { get; set; }
    [Key(1)] public ErrorCode ErrorCode { get; set; }
}
