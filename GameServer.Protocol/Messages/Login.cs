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
    // NOTE: 필드명은 클라이언트 호환성을 위해 PlayerId로 유지하지만,
    // 실제로는 DB의 account_id 값이 담긴다 (player_id ≠ account_id).
    [Key(0)] public ulong    PlayerId   { get; set; }
    [Key(1)] public string   PlayerName { get; set; } = string.Empty;
    [Key(2)] public ErrorCode ErrorCode { get; set; }
}
