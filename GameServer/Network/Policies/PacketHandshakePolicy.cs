using GameServer.Protocol;

namespace GameServer.Network.Policies;

/// <summary>
/// 패킷 수신 순서를 강제하는 핸드셰이크 정책.
/// 생성 시 지정한 순서대로 패킷이 수신되어야 하며, 순서가 맞지 않으면 위반으로 판정한다.
/// 시퀀스가 모두 소진되면 이후 모든 패킷은 통과한다.
///
/// 사용 예:
///   new PacketHandshakePolicy(
///       sequence:      [ReqSelectCharacter, ReqEnterStage],
///       ignorePackets: [ReqHeartbeat])
///
/// 현재 이 서버의 패킷 구조에서는 로그인 핸드셰이크를
/// GameServerHandler에서 별도 처리하므로 직접 사용하지 않는다.
/// 향후 다단계 입장 절차가 생기면 _packetPolicies 배열에 추가한다.
/// </summary>
public class PacketHandshakePolicy : IPacketPolicy
{
    private readonly Queue<GamePacket.PayloadOneofCase> _sequence;
    private readonly HashSet<GamePacket.PayloadOneofCase> _ignorePackets;

    private PacketHandshakePolicy(
        GamePacket.PayloadOneofCase[] sequence,
        GamePacket.PayloadOneofCase[]? ignorePackets)
    {
        _sequence = new Queue<GamePacket.PayloadOneofCase>(sequence);
        _ignorePackets = ignorePackets is not null
            ? new HashSet<GamePacket.PayloadOneofCase>(ignorePackets)
            : [];
    }

    public static PacketHandshakePolicy Create(
        GamePacket.PayloadOneofCase[] sequence,
        GamePacket.PayloadOneofCase[]? ignorePackets = null)
        => new(sequence, ignorePackets);

    public PacketPolicyResult VerifyPolicy(GamePacket.PayloadOneofCase packetType)
    {
        // 무시 목록에 있는 패킷은 항상 통과
        if (_ignorePackets.Contains(packetType))
            return PacketPolicyResult.DefaultSuccess;

        // 시퀀스 소진 — 핸드셰이크 완료, 이후 모든 패킷 통과
        if (_sequence.Count == 0)
            return PacketPolicyResult.DefaultSuccess;

        var expected = _sequence.Dequeue();
        var result = expected == packetType;

        return PacketPolicyResult.Create(
            result,
            nameof(PacketHandshakePolicy),
            packetType.ToString(),
            result ? string.Empty : $"Expected: {expected}, Actual: {packetType}");
    }

    public void UpdatePolicy(GamePacket.PayloadOneofCase packetType) { }

    public void Clear() => _sequence.Clear();
}
