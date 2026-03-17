namespace Common.Server.Component;

public sealed class WorkerSystem<T> : IDisposable where T : BaseComponent
{
    private readonly List<BaseWorker<T>> _workers;
    private readonly int _workerCount;

    public WorkerSystem(int workerCount, int intervalMs)
    {
        _workerCount = workerCount;
        _workers = new List<BaseWorker<T>>(workerCount);
        for (int i = 0; i < workerCount; i++)
        {
            _workers.Add(new BaseWorker<T>(intervalMs));
        }
    }

    private int GetIndex(T item) =>
        (int)((item.InstanceId & long.MaxValue) % _workerCount);

    public void Add(T item)
    {
        item.Initialize();
        _workers[GetIndex(item)].Add(item);
    }

    public void Remove(T item) => _workers[GetIndex(item)].Remove(item);

    public void StartSystem()
    {
        foreach (var worker in _workers)
        {
            worker.Initialize();
        }
    }

    public void Stop()
    {
        // 병렬 Stop: 각 Worker를 별도 스레드에서 Join
        var stopTasks = _workers.Select(w => Task.Run(() =>
        {
            try
            {
                w.Stop(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Common.Logging.GameLogger.Error("WorkerSystem", $"Stop error: {ex.Message}", ex);
            }
        }));
        // 서버 종료 경로 전용 — Task.Run으로 감싼 Thread.Join(동기)이므로 데드락 위험 없음
        Task.WhenAll(stopTasks).GetAwaiter().GetResult();
    }

    public void Dispose() => Stop();
}
