using System.Collections.Concurrent;
using Common.Logging;

namespace Common.Server.Component;

public abstract class BaseComponent : IDisposable
{
    private static long _idCounter;

    public long InstanceId { get; } = Interlocked.Increment(ref _idCounter);

    private readonly ConcurrentQueue<Action> _eventQueue = new();
    private int _disposed;
    protected bool IsDisposed => _disposed == 1;

    public void EnqueueEvent(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _eventQueue.Enqueue(job);
    }

    public virtual void Update(float dt)
    {
        if (_disposed == 1)
        {
            return;
        }

        while (_eventQueue.TryDequeue(out var job))
        {
            job();
        }
    }

    // 컴포넌트가 WorkerSystem에 등록될 때 호출되는 초기화 진입점.
    // 생성자 이후 2단계 셋업(라우터 등록, 자식 초기화 등)을 여기서 수행한다.
    public abstract void Initialize();

    protected abstract void OnDispose();

    public void Dispose()
    {
        // Interlocked.Exchange로 원자적 처리 — 이중 실행 방지
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Dispose 직전 enqueue된 이벤트(Broadcast 등) 유실 방지 — 잔류 큐 드레인
        while (_eventQueue.TryDequeue(out var job))
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                GameLogger.Error("BaseComponent", $"Dispose drain 중 예외 (InstanceId={InstanceId}): {ex.Message}", ex);
            }
        }
        
        OnDispose();
    }
}
