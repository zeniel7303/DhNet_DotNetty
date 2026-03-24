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

    private static readonly Dictionary<GamePacket.PayloadOneofCase, int> ExclusionMaxCount = new()
    {
        // 채팅/이동/전투 패킷은 연속 입력이 자연스러우므로 큐 상한을 높게 설정
        { GamePacket.PayloadOneofCase.ReqRoomChat,  5 },
        { GamePacket.PayloadOneofCase.ReqMove,      5 },
        { GamePacket.PayloadOneofCase.ReqAttack,    3 },
        { GamePacket.PayloadOneofCase.ReqGameChat,  3 },
    };

    // 큐에 적재된 특정 타입 패킷 수를 반환하는 delegate — SessionComponent에서 주입
    private readonly Func<GamePacket.PayloadOneofCase, int> _getQueueCount;

    private PacketPairPolicy(Func<GamePacket.PayloadOneofCase, int> getQueueCount)
        => _getQueueCount = getQueueCount;

    public static PacketPairPolicy Create(Func<GamePacket.PayloadOneofCase, int> getQueueCount)
        => new(getQueueCount);

    private static int GetMaxCount(GamePacket.PayloadOneofCase packetType)
        => ExclusionMaxCount.GetValueOrDefault(packetType, DefaultMaxCount);

    public PacketPolicyResult VerifyPolicy(GamePacket.PayloadOneofCase packetType)
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

    public void UpdatePolicy(GamePacket.PayloadOneofCase packetType) { }
    public void Clear() { }
}
