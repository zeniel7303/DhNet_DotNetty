namespace GameServer.Systems;

public class UniqueIdGenerator
{
    private long _counter = 0;

    public ulong Next() => (ulong)Interlocked.Increment(ref _counter);

    /// <summary>
    /// 서버 시작 시 DB max 값으로 카운터를 초기화한다.
    /// 이후 Next()는 startFrom+1 부터 반환한다.
    /// </summary>
    public void Initialize(ulong startFrom)
    {
        if (startFrom > (ulong)long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrom),
                $"ID 값이 long.MaxValue({long.MaxValue})를 초과합니다: {startFrom}");
        }
        Interlocked.Exchange(ref _counter, (long)startFrom);
    }
}
