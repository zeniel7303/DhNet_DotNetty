using System.Collections.Concurrent;
using Common.Logging;

namespace GameServer.World;

public sealed class DisconnectWork
{
    public required ulong AccountId { get; init; }
    public required ulong CharacterId { get; init; }
    public required IZoneDisconnect World { get; init; }
    public required Func<Task> Flush { get; init; }
    public required Action Remove { get; init; }
}

/// <summary>
/// BeginDisconnect 이후 SnapshotQueued를 제한 시간만 기다린 뒤 FlushAccount와 Remove를 진행한다.
/// 스냅샷이 그 시간 안에 없어도 Flush를 미루지 않는다.
/// Remove 이후의 Despawn·Upsert·SnapshotQueued는 존이 무시한다.
/// </summary>
public sealed class DisconnectOrchestrator
{
    private readonly TimeSpan _snapshotTimeout;
    private readonly ConcurrentDictionary<ulong, RunState> _inflight = new();

    public DisconnectOrchestrator(TimeSpan snapshotTimeout)
    {
        _snapshotTimeout = snapshotTimeout;
    }

    public async Task RunAsync(DisconnectWork work)
    {
        var run = new RunState();
        if (!_inflight.TryAdd(work.AccountId, run))
            return;

        try
        {
            try
            {
                work.World.BeginDisconnect(work.AccountId, work.CharacterId, run.SignalIfWaiting);
            }
            catch (Exception ex)
            {
                GameLogger.Error("DisconnectOrchestrator",
                    $"BeginDisconnect 실패 (AccountId={work.AccountId})", ex);
            }

            var completed = await Task.WhenAny(run.Signal.Task, Task.Delay(_snapshotTimeout));
            if (completed != run.Signal.Task)
            {
                GameLogger.Warn("DisconnectOrchestrator",
                    $"SnapshotQueued 시간 초과 — Flush 후 제거합니다 (AccountId={work.AccountId})");
            }

            if (!run.TryEnterFlush())
                return;

            try
            {
                await work.Flush();
            }
            catch (Exception ex)
            {
                GameLogger.Error("DisconnectOrchestrator",
                    $"FlushAccount 실패 (AccountId={work.AccountId})", ex);
            }
            finally
            {
                var removed = false;
                try
                {
                    work.Remove();
                    removed = true;
                }
                catch (Exception ex)
                {
                    GameLogger.Error("DisconnectOrchestrator",
                        $"세션 제거 실패 (AccountId={work.AccountId})", ex);
                }

                if (removed)
                {
                    try
                    {
                        work.World.MarkSessionRemoved(work.AccountId);
                    }
                    catch (Exception ex)
                    {
                        GameLogger.Error("DisconnectOrchestrator",
                            $"제거 표시 실패 (AccountId={work.AccountId})", ex);
                    }
                }
            }
        }
        finally
        {
            run.MarkTerminal();
            _inflight.TryRemove(work.AccountId, out _);
        }
    }

    private sealed class RunState
    {
        private int _phase;

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalIfWaiting()
        {
            if (Volatile.Read(ref _phase) != 0)
                return;

            Signal.TrySetResult();
        }

        public bool TryEnterFlush() => Interlocked.CompareExchange(ref _phase, 1, 0) == 0;

        public void MarkTerminal() => Volatile.Write(ref _phase, 2);
    }
}
