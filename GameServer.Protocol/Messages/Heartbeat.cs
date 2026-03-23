using MessagePack;

namespace GameServer.Protocol;

[MessagePackObject]
public sealed class ReqHeartbeat : IPacketPayload { }

[MessagePackObject]
public sealed class ResHeartbeat : IPacketPayload { }
