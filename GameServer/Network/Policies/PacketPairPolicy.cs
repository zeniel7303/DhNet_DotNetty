using Common.Logging;
using GameServer.Protocol;

namespace GameServer.Network.Policies;

/// <summary>
/// 패킷 타입별 큐 적재 수 제한 정책.
/// 큐에 이미 쌓인 동일 타입 패킷 수를 외부 delegate로 조회하여 한도 초과 여부를 판정한다.
/// 별도 카운터 없이 큐 상태를 직접 읽으므로 UpdatePolicy/Clear는 불필요(no-op).
///
/// 기본 한도: 1 / 예외 타입(RoomChat, LobbyChat): 5
/// </summary>
public class PacketPairPolicy : IPacketPolicy
{
    private const int DefaultMaxCount = 1;

    private static readonly Dictionary<PacketType, int> ExclusionMaxCount = new()
    {
        // 테스트용 예외 예시
        // { PacketType.ReqRoomChat,  5 },
        // { PacketType.ReqLobbyChat, 5 },
    };

    // 큐에 적재된 특정 타입 패킷 수를 반환하는 delegate — SessionComponent에서 주입
    private readonly Func<PacketType, int> _getQueueCount;

    private PacketPairPolicy(Func<PacketType, int> getQueueCount)
        => _getQueueCount = getQueueCount;

    public static PacketPairPolicy Create(Func<PacketType, int> getQueueCount)
        => new(getQueueCount);

    private static int GetMaxCount(PacketType packetType)
        => ExclusionMaxCount.GetValueOrDefault(packetType, DefaultMaxCount);

    public PacketPolicyResult VerifyPolicy(PacketType packetType)
    {
        var current = _getQueueCount(packetType);
        var maxCount = GetMaxCount(packetType);
        var ok = current < maxCount;

        return PacketPolicyResult.Create(
            ok,
            nameof(PacketPairPolicy),
            packetType.ToString(),
            ok ? string.Empty : $"QueueCount: {current}, MaxCount: {maxCount}");
    }

    public void UpdatePolicy(PacketType packetType) { }
    public void Clear() { }
}
