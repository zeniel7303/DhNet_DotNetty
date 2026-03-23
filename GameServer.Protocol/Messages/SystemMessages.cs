using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class NotiSystem : IPacketPayload
{
    [Key(0)] public string Message { get; set; } = string.Empty;
}
