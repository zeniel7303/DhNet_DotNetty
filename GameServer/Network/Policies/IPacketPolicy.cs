using GameServer.Protocol;

namespace GameServer.Network.Policies;

public record PacketPolicyResult
{
    public bool Result { get; private init; }
    public string PolicyName { get; private init; }
    public string PacketName { get; private init; }
    public string Message { get; private init; }

    private PacketPolicyResult() { PolicyName = string.Empty; PacketName = string.Empty; Message = string.Empty; }

    public static PacketPolicyResult Create(bool result, string policyName, string packetName, string message)
        => new() { Result = result, PolicyName = policyName, PacketName = packetName, Message = message };

    public static readonly PacketPolicyResult DefaultSuccess
        = Create(true, string.Empty, string.Empty, string.Empty);

    public override string ToString()
        => $"PolicyName: {PolicyName}, PacketName: {PacketName}, Message: {Message}";
}

/// <summary>
/// 패킷 수신/처리 정책 인터페이스.
/// VerifyPolicy: 수신 시 정책 검증 (I/O 이벤트 루프 스레드).
/// UpdatePolicy: 처리 완료 후 상태 갱신 (워커 스레드).
/// Clear: 세션 종료 시 상태 초기화.
/// </summary>
public interface IPacketPolicy
{
    PacketPolicyResult VerifyPolicy(GamePacket.PayloadOneofCase packetType);
    void UpdatePolicy(GamePacket.PayloadOneofCase packetType);
    void Clear();
}
