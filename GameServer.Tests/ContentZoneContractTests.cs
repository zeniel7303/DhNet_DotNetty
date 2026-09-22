using System.Reflection;
using System.Runtime.CompilerServices;
using GameServer.Database.Rows;
using GameServer.World;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// D2 계약: 틱에 DB await 없음, EnterZone 검증 후에만 스폰, Flush는 SnapshotQueued(또는 타임아웃) 이후.
/// </summary>
public class ContentZoneContractTests
{
    [Fact]
    public async Task EnterZone_RejectsUnknownToken_WithoutSpawn()
    {
        var zone = OpenZone();
        var done = Done();
        zone.PostEnter(Command("missing", characterId: 1, zone), done);
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.RejectedInvalidToken, (await done.Task).Status);
        Assert.Equal(0, zone.OccupantCount);
    }

    [Fact]
    public async Task EnterZone_RejectsMismatchedCharacter_WithoutSpawn()
    {
        var zone = OpenZone();
        var assignment = zone.AssignZone(accountId: 5, characterId: 5, ContentZone.RoomMapSpawn);
        var done = Done();
        zone.PostEnter(Command(assignment.SessionToken, characterId: 9, zone), done);
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.RejectedAssignmentMismatch, (await done.Task).Status);
        Assert.False(zone.IsSpawned(5));
    }

    [Fact]
    public async Task EnterZone_Rejects_WhenCanSpawnIsFalse()
    {
        var zone = OpenZone();
        var assignment = zone.AssignZone(3, 3, ContentZone.RoomMapSpawn);
        var done = Done();
        var command = Command(assignment.SessionToken, 3, zone);
        command.CanSpawn = () => false;
        zone.PostEnter(command, done);
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.RejectedZoneUnavailable, (await done.Task).Status);
        Assert.Equal(0, zone.OccupantCount);
    }

    [Fact]
    public async Task EnterZone_Rejects_WhenZoneIsNotOpen()
    {
        var zone = new ContentZone(new RecordingWriter());
        var done = Done();
        zone.PostEnter(Command("token", 1, zone), done);

        Assert.Equal(EnterZoneStatus.RejectedZoneUnavailable, (await done.Task).Status);
        Assert.Equal(0, zone.OccupantCount);
    }

    [Fact]
    public async Task EnterZone_Spawns_WhenTokenAndAssignmentMatch()
    {
        var zone = OpenZone();
        var assignment = zone.AssignZone(7, 7, ContentZone.RoomMapSpawn);
        var first = Done();
        var second = Done();
        zone.PostEnter(Command(assignment.SessionToken, 7, zone), first);
        zone.PostEnter(Command(assignment.SessionToken, 7, zone), second);
        zone.RunOneTick(0.1f);

        var firstResult = await first.Task;
        var secondResult = await second.Task;
        Assert.Equal(EnterZoneStatus.Spawned, firstResult.Status);
        Assert.Equal(EnterZoneStatus.Spawned, secondResult.Status);
        Assert.Equal(firstResult.EntityId, secondResult.EntityId);
        Assert.Equal(1, zone.OccupantCount);
    }

    [Fact]
    public async Task LeaveZone_EnqueuesSnapshot_ThenAllowsReenter()
    {
        var writer = new RecordingWriter();
        var zone = OpenZone(writer);
        var assignment = zone.AssignZone(2, 2, ContentZone.RoomMapSpawn);
        var entered = Done();
        zone.PostEnter(Command(assignment.SessionToken, 2, zone, () => Row(2, 11)), entered);
        zone.RunOneTick(0.1f);

        zone.LeaveZone(2);
        zone.RunOneTick(0.1f);

        Assert.False(zone.IsSpawned(2));
        Assert.Equal(11, writer.Rows.Single().gold);
        Assert.Equal(0, writer.FlushCalls);

        var again = Done();
        zone.PostEnter(Command(assignment.SessionToken, 2, zone), again);
        zone.RunOneTick(0.1f);
        Assert.Equal(EnterZoneStatus.Spawned, (await again.Task).Status);
        Assert.True(zone.IsSpawned(2));
    }

    [Fact]
    public async Task Tick_EnqueuesDirtySnapshot_WithoutFlushOrAwait()
    {
        AssertTickPathIsSynchronous();

        var writer = new RecordingWriter();
        var zone = new ContentZone(writer, snapshotIntervalSeconds: 0f);
        zone.Open();
        var assignment = zone.AssignZone(8, 8, ContentZone.RoomMapSpawn);
        CharacterRow? pending = Row(8, 4);
        var entered = Done();
        var command = Command(assignment.SessionToken, 8, zone);
        command.ConsumeDirty = () =>
        {
            var row = pending;
            pending = null;
            return row;
        };
        zone.PostEnter(command, entered);

        zone.RunOneTick(0.1f);
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.Spawned, (await entered.Task).Status);
        Assert.Equal(4, writer.Rows.Single().gold);
        Assert.Equal(0, writer.FlushCalls);
    }

    [Fact]
    public async Task Tick_IsolatesSnapshotException()
    {
        var zone = new ContentZone(new RecordingWriter(), snapshotIntervalSeconds: 0f);
        zone.Open();
        var assignment = zone.AssignZone(1, 1, ContentZone.RoomMapSpawn);
        var entered = Done();
        var command = Command(assignment.SessionToken, 1, zone);
        command.ConsumeDirty = () => throw new InvalidOperationException("snapshot failed");
        zone.PostEnter(command, entered);

        var ex = Record.Exception(() => zone.RunOneTick(0.1f));

        Assert.Null(ex);
        Assert.Equal(EnterZoneStatus.Spawned, (await entered.Task).Status);
    }

    [Fact]
    public async Task Aoi_DeliversEnterToEverySubscriberInZone()
    {
        var zone = OpenZone();
        var firstAssignment = zone.AssignZone(1, 1, ContentZone.RoomMapSpawn);
        var secondAssignment = zone.AssignZone(2, 2, ContentZone.RoomMapSpawn);
        var firstEvents = new List<ZoneEvent>();
        var secondEvents = new List<ZoneEvent>();
        var first = Done();
        var second = Done();
        var firstCommand = Command(firstAssignment.SessionToken, 1, zone);
        firstCommand.Deliver = firstEvents.Add;
        var secondCommand = Command(secondAssignment.SessionToken, 2, zone);
        secondCommand.Deliver = secondEvents.Add;
        zone.PostEnter(firstCommand, first);
        zone.PostEnter(secondCommand, second);
        zone.RunOneTick(0.1f);

        var secondEntity = (await second.Task).EntityId;
        var firstEntity = (await first.Task).EntityId;
        Assert.Contains(firstEvents, e => e.Kind == ZoneEventKind.Entered && e.EntityId == secondEntity);
        Assert.Contains(secondEvents, e => e.Kind == ZoneEventKind.Snapshot && e.EntityId == firstEntity);

        zone.LeaveZone(1);
        zone.RunOneTick(0.1f);
        Assert.Contains(secondEvents, e => e.Kind == ZoneEventKind.Left && e.EntityId == firstEntity);
        Assert.DoesNotContain(firstEvents, e => e.Kind == ZoneEventKind.Left);
    }

    [Fact(Timeout = 3000)]
    public async Task Disconnect_FlushThenRemove_AfterSnapshot()
    {
        var order = new List<string>();
        var writer = new RecordingWriter(() => order.Add("upsert"));
        var zone = OpenZone(writer);
        await Spawn(zone, 4, () => Row(4, 8));

        var orchestrator = new DisconnectOrchestrator(TimeSpan.FromSeconds(2));
        var task = orchestrator.RunAsync(Work(zone, 4, order));
        zone.RunOneTick(0.1f);
        await task;

        Assert.Equal(new[] { "upsert", "flush", "remove" }, order);
        Assert.False(zone.IsSpawned(4));
        Assert.Equal(0, writer.FlushCalls);
    }

    [Fact(Timeout = 3000)]
    public async Task Disconnect_OnSnapshotTimeout_StillFlushThenRemove_AndIgnoresLateSignal()
    {
        var order = new List<string>();
        var writer = new RecordingWriter(() => order.Add("upsert"));
        var zone = OpenZone(writer);
        await Spawn(zone, 6, () => Row(6, 1));

        var flushCount = 0;
        var removeCount = 0;
        var orchestrator = new DisconnectOrchestrator(TimeSpan.FromMilliseconds(50));
        await orchestrator.RunAsync(new DisconnectWork
        {
            AccountId = 6,
            CharacterId = 6,
            World = zone,
            Flush = () =>
            {
                flushCount++;
                order.Add("flush");
                return Task.CompletedTask;
            },
            Remove = () =>
            {
                removeCount++;
                order.Add("remove");
            },
        });

        Assert.Equal(new[] { "flush", "remove" }, order);
        zone.RunOneTick(0.1f);

        Assert.Equal(1, flushCount);
        Assert.Equal(1, removeCount);
        Assert.DoesNotContain("upsert", order);
        Assert.Empty(writer.Rows);
        Assert.False(zone.IsSpawned(6));
    }

    [Fact(Timeout = 3000)]
    public async Task Disconnect_AfterRemove_LateWorkDoesNotReplaceNewSpawn()
    {
        var order = new List<string>();
        var writer = new RecordingWriter(() => order.Add("upsert"));
        var zone = OpenZone(writer);
        await Spawn(zone, 6, () => Row(6, 1));

        var orchestrator = new DisconnectOrchestrator(TimeSpan.FromMilliseconds(30));
        await orchestrator.RunAsync(Work(zone, 6, order));

        var assignment = zone.AssignZone(6, 6, ContentZone.RoomMapSpawn);
        var again = Done();
        zone.PostEnter(Command(assignment.SessionToken, 6, zone, () => Row(6, 9)), again);
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.Spawned, (await again.Task).Status);
        Assert.True(zone.IsSpawned(6));
        Assert.DoesNotContain("upsert", order);
        Assert.Empty(writer.Rows);
    }

    [Fact(Timeout = 3000)]
    public async Task EnterZoneAsync_TimeoutBeforeLoop_DoesNotLeaveSpawn()
    {
        var writer = new RecordingWriter();
        var zone = new ContentZone(writer, enterTimeout: TimeSpan.FromMilliseconds(40));
        zone.Open();
        var assignment = zone.AssignZone(4, 4, ContentZone.RoomMapSpawn);

        var result = await zone.EnterZoneAsync(Command(assignment.SessionToken, 4, zone, () => Row(4, 3)));
        zone.RunOneTick(0.1f);

        Assert.Equal(EnterZoneStatus.RejectedZoneUnavailable, result.Status);
        Assert.False(zone.IsSpawned(4));
        Assert.Empty(writer.Rows);
    }

    [Fact(Timeout = 8000)]
    public async Task EnterZoneAsync_TimeoutDuringSpawn_RollsBackOccupant()
    {
        var writer = new RecordingWriter();
        var zone = new ContentZone(writer, enterTimeout: TimeSpan.FromMilliseconds(400));
        zone.Open();
        var assignment = zone.AssignZone(4, 4, ContentZone.RoomMapSpawn);
        var inCanSpawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var command = Command(assignment.SessionToken, 4, zone, () => Row(4, 3));
        command.CanSpawn = () =>
        {
            inCanSpawn.TrySetResult();
            release.Wait();
            return true;
        };

        var enterTask = Task.Run(() => zone.EnterZoneAsync(command));
        var tickTask = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!inCanSpawn.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                zone.RunOneTick(0.1f);
                Thread.Sleep(5);
            }
        });

        await inCanSpawn.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await enterTask;
        Assert.Equal(EnterZoneStatus.RejectedZoneUnavailable, result.Status);
        release.Set();
        await tickTask;

        zone.RunOneTick(0.1f);
        Assert.False(zone.IsSpawned(4));
        Assert.Equal(0, zone.OccupantCount);
        Assert.Empty(writer.Rows);
    }

    private static void AssertTickPathIsSynchronous()
    {
        AssertNotAsync(typeof(ZoneLoop), nameof(ZoneLoop.RunOneTick));
        AssertNotAsync(typeof(ZoneLoop), "Loop");
        AssertNotAsync(typeof(ContentZone), "OnTick");
        AssertNotAsync(typeof(ContentZone), nameof(ContentZone.BeginDisconnect));
        AssertNotAsync(typeof(ContentZone), nameof(ContentZone.LeaveZone));
        AssertNotAsync(typeof(GatewayZoneSnapshotWriter), nameof(GatewayZoneSnapshotWriter.Enqueue));
    }

    private static void AssertNotAsync(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Null(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    private static ContentZone OpenZone(RecordingWriter? writer = null)
    {
        var zone = new ContentZone(writer ?? new RecordingWriter());
        zone.Open();
        return zone;
    }

    private static async Task Spawn(ContentZone zone, ulong accountId, Func<CharacterRow?> read)
    {
        var assignment = zone.AssignZone(accountId, accountId, ContentZone.RoomMapSpawn);
        var done = Done();
        zone.PostEnter(Command(assignment.SessionToken, accountId, zone, read), done);
        zone.RunOneTick(0.1f);
        Assert.Equal(EnterZoneStatus.Spawned, (await done.Task).Status);
    }

    private static DisconnectWork Work(ContentZone zone, ulong accountId, List<string> order)
        => new()
        {
            AccountId = accountId,
            CharacterId = accountId,
            World = zone,
            Flush = () =>
            {
                order.Add("flush");
                return Task.CompletedTask;
            },
            Remove = () => order.Add("remove"),
        };

    private static EnterZoneCommand Command(string token, ulong characterId, ContentZone zone, Func<CharacterRow?>? read = null)
        => new()
        {
            SessionToken = token,
            CharacterId = characterId,
            WorldId = "main",
            ZoneId = "room-map",
            ReadSnapshot = read,
        };

    private static TaskCompletionSource<EnterZoneResult> Done()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CharacterRow Row(ulong accountId, int gold)
        => new() { account_id = accountId, gold = gold };

    private sealed class RecordingWriter : IZoneSnapshotWriter
    {
        private readonly Action? _onEnqueue;

        public RecordingWriter(Action? onEnqueue = null) => _onEnqueue = onEnqueue;

        public List<CharacterRow> Rows { get; } = new();
        public int FlushCalls { get; private set; }

        public void Enqueue(CharacterRow row)
        {
            Rows.Add(row);
            _onEnqueue?.Invoke();
        }

        public void Flush() => FlushCalls++;
    }
}
