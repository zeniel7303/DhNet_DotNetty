using System.Collections.Concurrent;
using Common.Logging;

namespace GameServer.World;

/// <summary>
/// 단일 존의 인프로세스 루프. 수신 메시지를 비운 뒤 시뮬레이션을 한 번 진행하고 쉰다.
/// 틱 메서드는 DB·디스크를 기다리지 않는다.
/// </summary>
public sealed class ZoneLoop
{
    public const int TickIntervalMs = 100;

    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly Action<float> _onTick;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private int _started;

    public ZoneLoop(Action<float> onTick)
    {
        _onTick = onTick;
    }

    public void Post(Action action) => _inbox.Enqueue(action);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "ZoneLoop",
        };
        _thread.Start();
    }

    public void StopAndDrain()
    {
        _cts?.Cancel();
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(5)))
            GameLogger.Error("ZoneLoop", "스레드가 5초 안에 멈추지 않았습니다.");

        DrainInbox();
    }

    /// <summary>한 틱. 호출 스레드에서 끝나고, Task를 반환하지 않는다.</summary>
    public void RunOneTick(float dtSeconds)
    {
        DrainInbox();
        try
        {
            _onTick(dtSeconds);
        }
        catch (Exception ex)
        {
            GameLogger.Error("ZoneLoop", "틱 예외를 격리했습니다.", ex);
        }
    }

    private void Loop(CancellationToken token)
    {
        var dt = TickIntervalMs / 1000f;
        while (!token.IsCancellationRequested)
        {
            RunOneTick(dt);
            Thread.Sleep(TickIntervalMs);
        }
    }

    private void DrainInbox()
    {
        while (_inbox.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                GameLogger.Error("ZoneLoop", "수신 메시지 예외를 격리했습니다.", ex);
            }
        }
    }
}
