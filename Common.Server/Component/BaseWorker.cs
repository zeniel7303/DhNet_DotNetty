using System.Collections.Concurrent;
using Common.Logging;

namespace Common.Server.Component;

public sealed class BaseWorker<T> where T : BaseComponent
{
    private static readonly float TicksToSeconds = 1.0f / TimeSpan.TicksPerSecond;

    private readonly int _intervalMs;
    private Thread? _thread;
    private bool _running;

    private sealed class WorkerItem(T item)
    {
        public readonly T Item = item;
        public long LastTicks = DateTime.UtcNow.Ticks;
    }

    private readonly ConcurrentDictionary<long, WorkerItem> _items = new();

    public BaseWorker(int intervalMs)
    {
        _intervalMs = intervalMs;
    }

    public void Add(T item)
    {
        _items[item.InstanceId] = new WorkerItem(item);
    }

    public void Remove(T item)
    {
        _items.TryRemove(item.InstanceId, out _);
    }

    public void Initialize()
    {
        if (_thread is { IsAlive: true }) return;

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop(TimeSpan timeout)
    {
        _running = false;
        _thread?.Join(timeout);
    }

    private void Loop()
    {
        while (_running)
        {
            TickInternal();
            Thread.Sleep(_intervalMs);
        }
    }

    private void TickInternal()
    {
        foreach (var entry in _items.Values)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var dt = (nowTicks - entry.LastTicks) * TicksToSeconds;
            entry.LastTicks = nowTicks;

            try
            {
                entry.Item.Update(dt);
            }
            catch (Exception ex)
            {
                GameLogger.Error("BaseWorker", $"Update error on {entry.Item.InstanceId}", ex);
            }
        }
    }
}
