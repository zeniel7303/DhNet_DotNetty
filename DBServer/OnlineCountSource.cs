namespace DBServer;

/// <summary>GameServer가 1초마다 보고하는 온라인 인원.</summary>
public sealed class OnlineCountSource
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Set(int count) => Volatile.Write(ref _count, count);
}
