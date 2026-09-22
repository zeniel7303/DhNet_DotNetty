namespace GameServer.Database.Gateway;

/// <summary>
/// 비동기 저장 큐의 재시도·내구성·장애 임계치.
/// 버퍼는 512MiB의 80% 또는 1만 건, dirty는 온라인의 20% 또는 50세션,
/// DB 성공이 60초 동안 없으면 신규 로그인을 막고 dirty 세션을 끊는다.
/// </summary>
internal sealed class DurableSaveOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromSeconds(60);
    public long CapacityBytes { get; init; } = 512L * 1024 * 1024;
    public double BufferPressureRatio { get; init; } = 0.8;
    public int MaxRecords { get; init; } = 10_000;
    public double DirtySessionRatio { get; init; } = 0.2;
    public int DirtySessionCount { get; init; } = 50;

    public static DurableSaveOptions Default { get; } = new();

    public long BufferPressureBytes()
    {
        if (CapacityBytes <= 0 || BufferPressureRatio <= 0)
        {
            return 0;
        }

        var scaled = (double)CapacityBytes * BufferPressureRatio;
        if (scaled >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)scaled;
    }
}
