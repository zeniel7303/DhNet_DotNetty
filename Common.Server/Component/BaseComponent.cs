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

    // disposed이면 false 반환 — EnqueueEventAsync가 TCS를 취소할 수 있도록 성공 여부 전달.
    protected bool EnqueueEvent(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        
        if (_disposed == 1)
        {
            return false;
        }
        
        _eventQueue.Enqueue(job);
        return true;
    }

    // 워커 스레드에서 action 실행 후 완료를 await할 수 있는 오버로드.
    // finally로 TrySetResult 보장 — action 내부 예외나 DisconnectForNextTick 호출 시에도 호출자가 무한 대기하지 않음.
    // EnqueueEvent가 false를 반환하면(disposed) TCS를 즉시 취소하여 호출자 무한 대기 방지.
    public Task EnqueueEventAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = EnqueueEvent(() =>
        {
            try
            {
                action();
            }
            finally
            {
                tcs.TrySetResult();
            }
        });

        if (!enqueued)
        {
            tcs.TrySetCanceled();
        }
        
        return tcs.Task;
    }

    // 워커 스레드에서 func 실행 후 반환값을 await할 수 있는 오버로드.
    // 예외 발생 시 TrySetException으로 전파 — 호출자에서 try/catch로 처리 가능.
    // EnqueueEvent가 false를 반환하면(disposed) TCS를 즉시 취소하여 호출자 무한 대기 방지.
    public Task<T> EnqueueEventAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = EnqueueEvent(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        
        if (!enqueued)
        {
            tcs.TrySetCanceled();
        }
        
        return tcs.Task;
    }

    public virtual void Update(float dt)
    {
        if (_disposed == 1)
        {
            return;
        }

        // 이벤트 하나의 예외가 나머지 이벤트 드롭으로 이어지지 않도록 개별 try/catch.
        while (_eventQueue.TryDequeue(out var job))
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                GameLogger.Error("BaseComponent", $"Update 이벤트 처리 중 예외 (InstanceId={InstanceId}): {ex.Message}", ex);
            }
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
