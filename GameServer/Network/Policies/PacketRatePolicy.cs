using System.Diagnostics;
using GameServer.Protocol;

namespace GameServer.Network.Policies;

/// <summary>
/// Sliding Window 알고리즘 기반 패킷 Rate Limiting 정책.
/// 1초 윈도우 내 수신된 패킷 수가 MaxPerSecond를 초과하면 위반으로 판정한다.
/// VerifyPolicy는 I/O 이벤트 루프(단일 스레드)에서만 호출되므로 별도 잠금 불필요.
/// </summary>
public class PacketRatePolicy : IPacketPolicy
{
    public const int DefaultMaxPerSecond = 60;

    private const double TimeWindowSeconds = 1.0;

    private readonly int _maxPacketsPerSecond;
    private readonly Queue<long> _packetTimestamps;
    private readonly Stopwatch _stopwatch;

    private PacketRatePolicy(int maxPacketsPerSecond)
    {
        _maxPacketsPerSecond = maxPacketsPerSecond;
        _packetTimestamps = new Queue<long>(maxPacketsPerSecond);
        _stopwatch = Stopwatch.StartNew();
    }

    public static PacketRatePolicy Create(int maxPerSecond = DefaultMaxPerSecond) => new(maxPerSecond);

    public PacketPolicyResult VerifyPolicy(PacketType packetType)
    {
        // 현재 시간을 밀리초 단위로 가져옴
        var currentTimeMs = _stopwatch.ElapsedMilliseconds;
        // 시간 윈도우 경계를 밀리초로 계산 (예: 1초 = 1000ms)
        var timeWindowMs = (long)(TimeWindowSeconds * 1000);

        // Sliding Window 알고리즘: 현재 시간 기준으로 윈도우 밖의 오래된 패킷 타임스탬프를 제거
        while (_packetTimestamps.Count > 0 && currentTimeMs - _packetTimestamps.Peek() >= timeWindowMs)
        {
            _packetTimestamps.Dequeue();
        }

        var currentPacketCount = _packetTimestamps.Count;
        var result = currentPacketCount < _maxPacketsPerSecond;

        if (result)
        {
            _packetTimestamps.Enqueue(currentTimeMs);
        }

        return PacketPolicyResult.Create(
            result,
            nameof(PacketRatePolicy),
            packetType.ToString(),
            result ? string.Empty : $"Value: {currentPacketCount + 1}, MaxCount: {_maxPacketsPerSecond}");
    }

    // Rate 정책은 시간 기반이므로 처리 후 별도 갱신 불필요
    public void UpdatePolicy(PacketType packetType) { }

    public void Clear()
    {
        _packetTimestamps.Clear();
        _stopwatch.Stop();
    }
}
