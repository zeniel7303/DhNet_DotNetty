using System.Reflection;
using GameServer.Database.Gateway;
using GameServer.Database.Rows;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// 캐릭터 저장 큐: 직렬화, 재시도, WAL, 지속 장애 임계치.
/// </summary>
public class DurableSaveQueueTests
{
    [Fact(Timeout = 5000)]
    public async Task Enqueue_Returns_Before_Db_Completes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = CreateQueue(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });

        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            queue.EnqueueCharacter(1, 10);
            returned.TrySetResult();
        });

        try
        {
            var finished = await Task.WhenAny(returned.Task, Task.Delay(2000));
            Assert.Same(returned.Task, finished);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
        }

        await queue.FlushAccountAsync(1);
    }

    [Fact(Timeout = 5000)]
    public async Task SameAccount_Coalesces_Gold_Before_Dispatch()
    {
        var golds = new List<int>();
        await using var queue = CreateQueue(work =>
        {
            if (work is SaveWork.Character character)
            {
                golds.Add(character.Gold);
            }

            return Task.CompletedTask;
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Hold(gate.Task);
        queue.EnqueueCharacter(1, 1);
        queue.EnqueueCharacter(1, 2);
        queue.EnqueueCharacter(1, 3);
        gate.SetResult();

        await queue.FlushAccountAsync(1);

        Assert.Equal(new[] { 3 }, golds);
    }

    [Fact(Timeout = 5000)]
    public async Task SameAccount_Writes_Gold_Then_Logout_In_Order()
    {
        var order = new List<string>();
        await using var queue = CreateQueue(work =>
        {
            order.Add(work switch
            {
                SaveWork.Character character => $"gold:{character.Gold}",
                SaveWork.Logout => "logout",
                _ => work.GetType().Name
            });
            return Task.CompletedTask;
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Hold(gate.Task);
        queue.EnqueueCharacter(4, 10);
        queue.EnqueueLogout(4, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        gate.SetResult();

        await queue.FlushAccountAsync(4);

        Assert.Equal(new[] { "gold:10", "logout" }, order);
    }

    [Fact(Timeout = 5000)]
    public async Task SameAccount_Does_Not_Overlap_In_Flight_Saves()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapped = 0;
        var inFlight = 0;
        var golds = new List<int>();

        await using var queue = CreateQueue(async work =>
        {
            var gold = ((SaveWork.Character)work).Gold;
            if (Interlocked.Increment(ref inFlight) > 1)
            {
                Interlocked.Increment(ref overlapped);
            }

            lock (golds)
            {
                golds.Add(gold);
            }

            if (gold == 1)
            {
                firstEntered.TrySetResult();
                await release.Task;
            }

            Interlocked.Decrement(ref inFlight);
        });

        try
        {
            queue.EnqueueCharacter(1, 1);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.EnqueueCharacter(1, 2);
            release.TrySetResult();
            await queue.FlushAccountAsync(1);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(0, overlapped);
        Assert.Equal(new[] { 1, 2 }, golds);
    }

    [Fact(Timeout = 5000)]
    public async Task Retry_Uses_Exponential_Backoff_Then_Parks()
    {
        var calls = 0;
        var delays = new List<double>();
        await using var queue = CreateQueue(
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new IOException("db down");
            },
            options: new DurableSaveOptions
            {
                MaxRetries = 3,
                InitialBackoff = TimeSpan.FromMilliseconds(200),
                FailureWindow = TimeSpan.FromDays(1),
                MaxRecords = int.MaxValue,
                CapacityBytes = 512L * 1024 * 1024,
                DirtySessionCount = int.MaxValue,
                DirtySessionRatio = 1
            },
            delay: (time, token) =>
            {
                token.ThrowIfCancellationRequested();
                delays.Add(time.TotalMilliseconds);
                return Task.CompletedTask;
            });

        queue.EnqueueCharacter(3, 9);
        await queue.FlushAccountAsync(3);

        Assert.Equal(4, calls);
        Assert.Equal(new[] { 200d, 400d, 800d }, delays);
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.ParkedAccountCount);
    }

    [Fact(Timeout = 5000)]
    public async Task Wal_Replays_Character_After_Restart()
    {
        var dir = NewWalDir();
        try
        {
            await using (var failed = CreateQueue(_ => throw new IOException("db down"), dir, QuietOptions(maxRetries: 0)))
            {
                failed.EnqueueCharacter(5, 42);
                await failed.FlushAccountAsync(5);
                Assert.NotEmpty(Directory.EnumerateFiles(dir, "c-*.json"));
            }

            var golds = new List<int>();
            await using var recovered = CreateQueue(work =>
            {
                if (work is SaveWork.Character character)
                {
                    golds.Add(character.Gold);
                }

                return Task.CompletedTask;
            }, dir, QuietOptions(maxRetries: 0));
            recovered.Recover();
            await recovered.FlushAccountAsync(5);

            Assert.Equal(new[] { 42 }, golds);
            Assert.Empty(Directory.EnumerateFiles(dir, "*.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Continuous_Failure_Blocks_Login_And_Notifies_Dirty_Accounts()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notified = 0;
        List<ulong> parked = new();
        await using var queue = CreateQueue(
            _ => throw new IOException("db down"),
            options: QuietOptions(maxRetries: 0, failureWindow: TimeSpan.FromSeconds(60)),
            utcNow: () => now);
        queue.OnSustainedOutage = ids =>
        {
            notified++;
            parked = ids.ToList();
        };

        queue.EnqueueCharacter(7, 1);
        await queue.FlushAccountAsync(7);
        Assert.False(queue.IsLoginBlocked);

        now = now.AddSeconds(60);
        queue.Evaluate();

        Assert.True(queue.IsLoginBlocked);
        Assert.Equal(1, notified);
        Assert.Contains(7ul, parked);

        queue.Evaluate();
        Assert.Equal(1, notified);
    }

    [Fact(Timeout = 5000)]
    public async Task Buffer_Record_Limit_Blocks_Login()
    {
        await using var queue = CreateQueue(
            _ => throw new IOException("db down"),
            options: QuietOptions(maxRetries: 0, maxRecords: 2));

        queue.EnqueueCharacter(1, 1);
        await queue.FlushAccountAsync(1);
        Assert.False(queue.IsLoginBlocked);

        queue.EnqueueCharacter(2, 1);
        await queue.FlushAccountAsync(2);

        Assert.True(queue.IsLoginBlocked);
        Assert.True(queue.PendingCount >= 2);
    }

    [Fact(Timeout = 5000)]
    public async Task Buffer_Byte_Pressure_Blocks_Login()
    {
        await using var queue = CreateQueue(
            _ => throw new IOException("db down"),
            options: new DurableSaveOptions
            {
                MaxRetries = 0,
                FailureWindow = TimeSpan.FromDays(1),
                CapacityBytes = 1,
                BufferPressureRatio = 0.8,
                MaxRecords = int.MaxValue,
                DirtySessionCount = int.MaxValue,
                DirtySessionRatio = 1
            });

        queue.EnqueueCharacter(1, 1);
        await queue.FlushAccountAsync(1);

        Assert.True(queue.IsLoginBlocked);
    }

    [Fact(Timeout = 5000)]
    public async Task Dirty_Count_And_Ratio_Block_Login()
    {
        await using var byCount = CreateQueue(
            _ => throw new IOException("db down"),
            options: QuietOptions(maxRetries: 0, dirtyCount: 2, dirtyRatio: 1));
        byCount.OnlineCount = () => 100;

        byCount.EnqueueCharacter(1, 1);
        await byCount.FlushAccountAsync(1);
        Assert.False(byCount.IsLoginBlocked);

        byCount.EnqueueCharacter(2, 1);
        await byCount.FlushAccountAsync(2);
        Assert.True(byCount.IsLoginBlocked);

        await using var byRatio = CreateQueue(
            _ => throw new IOException("db down"),
            options: QuietOptions(maxRetries: 0, dirtyCount: 1000, dirtyRatio: 0.2));
        byRatio.OnlineCount = () => 10;

        byRatio.EnqueueCharacter(1, 1);
        await byRatio.FlushAccountAsync(1);
        Assert.False(byRatio.IsLoginBlocked);

        byRatio.EnqueueCharacter(2, 1);
        await byRatio.FlushAccountAsync(2);
        Assert.True(byRatio.IsLoginBlocked);
    }

    [Fact(Timeout = 5000)]
    public async Task Success_Clears_Login_Block()
    {
        var calls = 0;
        await using var queue = CreateQueue(
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new IOException("db down");
                }

                return Task.CompletedTask;
            },
            options: QuietOptions(maxRetries: 0, maxRecords: 1));

        queue.EnqueueCharacter(8, 3);
        await queue.FlushAccountAsync(8);
        Assert.True(queue.IsLoginBlocked);

        queue.RetryParked();
        await queue.FlushAccountAsync(8);

        Assert.False(queue.IsLoginBlocked);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void CharacterRow_Persists_Account_And_Gold_Only()
    {
        var names = typeof(CharacterRow)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "account_id", "gold" }, names);
    }

    private static DurableSaveQueue CreateQueue(
        Func<SaveWork, Task> sink,
        string? directory = null,
        DurableSaveOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new DurableSaveQueue(
            directory ?? NewWalDir(),
            (work, _) => sink(work),
            options ?? QuietOptions(),
            utcNow,
            delay);
    }

    private static DurableSaveOptions QuietOptions(
        int maxRetries = 0,
        TimeSpan? failureWindow = null,
        int maxRecords = int.MaxValue,
        int dirtyCount = int.MaxValue,
        double dirtyRatio = 1)
        => new()
        {
            MaxRetries = maxRetries,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            FailureWindow = failureWindow ?? TimeSpan.FromDays(1),
            CapacityBytes = 512L * 1024 * 1024,
            BufferPressureRatio = 0.8,
            MaxRecords = maxRecords,
            DirtySessionCount = dirtyCount,
            DirtySessionRatio = dirtyRatio
        };

    private static string NewWalDir()
        => Path.Combine(Path.GetTempPath(), "dhnet-wal-" + Guid.NewGuid().ToString("N"));
}
