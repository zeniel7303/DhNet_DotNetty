namespace GameServer.World;

/// <summary>
/// D2 AOI 1종: 존 안 모든 구독자에게 전달한다.
/// 그리드와 반경 필터는 두지 않는다.
/// </summary>
public sealed class ZoneWideAoi
{
    private readonly Dictionary<long, Action<ZoneEvent>> _observers = new();

    public void Subscribe(long observerEntityId, Action<ZoneEvent> deliver)
        => _observers[observerEntityId] = deliver;

    public void Unsubscribe(long observerEntityId)
        => _observers.Remove(observerEntityId);

    public void Publish(ZoneEvent zoneEvent)
    {
        foreach (var deliver in _observers.Values)
            deliver(zoneEvent);
    }
}
