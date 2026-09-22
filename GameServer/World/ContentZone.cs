using System.Collections.Concurrent;
using Common;
using Common.Logging;
using GameServer.Database.Rows;

namespace GameServer.World;

/// <summary>
/// 현 룸 맵 하나에 대응하는 단일 콘텐츠 존.
/// 스폰·AOI·스냅샷 적재는 이 존의 ZoneLoop만 수행한다.
/// RPG 룸 전투 틱은 RoomSystem에 남긴다.
/// LeaveZone은 구현한다. TransferZone은 D4 이름 예약이며 메서드를 두지 않는다.
/// </summary>
public sealed class ContentZone : IZoneDisconnect
{
    /// <summary>Stage 스폰 포인트와 같은 룸 맵 입장 위치.</summary>
    public static readonly ZonePose RoomMapSpawn = new(1500f, 1200f, 0f, 0f);

    public static ContentZone Shared { get; } = new(new GatewayZoneSnapshotWriter());

    private readonly IZoneSnapshotWriter _snapshots;
    private readonly ZoneAssignmentRegistry _assignments = new();
    private readonly ZoneWideAoi _aoi = new();
    private readonly ZoneLoop _loop;
    private readonly Dictionary<ulong, Occupant> _occupants = new();
    private readonly ConcurrentDictionary<ulong, byte> _spawned = new();
    private readonly ConcurrentDictionary<ulong, int> _epoch = new();
    private readonly float _snapshotIntervalSeconds;
    private float _snapshotAcc;
    private long _nextEntityId;
    private int _open;
    private string _worldId = "main";
    private string _zoneId = "room-map";
    private TimeSpan _enterTimeout = TimeSpan.FromSeconds(3);

    public ContentZone(
        IZoneSnapshotWriter snapshots,
        float snapshotIntervalSeconds = 60f,
        TimeSpan? enterTimeout = null)
    {
        _snapshots = snapshots;
        _snapshotIntervalSeconds = snapshotIntervalSeconds;
        if (enterTimeout is { } timeout)
            _enterTimeout = timeout;
        _loop = new ZoneLoop(OnTick);
    }

    public bool IsOpen => Volatile.Read(ref _open) == 1;

    public int OccupantCount => _spawned.Count;

    public bool IsSpawned(ulong accountId) => _spawned.ContainsKey(accountId);

    public void Apply(GameServerSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WorldId))
            _worldId = settings.WorldId;
        if (!string.IsNullOrWhiteSpace(settings.ZoneId))
            _zoneId = settings.ZoneId;

        var seconds = settings.SnapshotQueuedTimeoutSeconds > 0
            ? settings.SnapshotQueuedTimeoutSeconds
            : 3;
        _enterTimeout = TimeSpan.FromSeconds(seconds);
    }

    public void Open() => Volatile.Write(ref _open, 1);

    public void Start()
    {
        Open();
        _loop.Start();
    }

    public void StopAndDrain() => _loop.StopAndDrain();

    public void RunOneTick(float dtSeconds) => _loop.RunOneTick(dtSeconds);

    public ZoneAssignment AssignZone(ulong accountId, ulong characterId, ZonePose pose)
        => _assignments.Assign(accountId, characterId, _worldId, _zoneId, pose);

    public void PostEnter(EnterZoneCommand command, TaskCompletionSource<EnterZoneResult> done)
    {
        if (!IsOpen)
        {
            done.TrySetResult(Unavailable());
            return;
        }

        _loop.Post(() =>
        {
            try
            {
                done.TrySetResult(EnterOnLoop(command).Result);
            }
            catch (Exception ex)
            {
                GameLogger.Error("ContentZone", "EnterZone 실패", ex);
                done.TrySetResult(Unavailable());
            }
        });
    }

    public async Task<EnterZoneResult> EnterZoneAsync(EnterZoneCommand command)
    {
        if (!IsOpen)
            return Unavailable();

        var done = new TaskCompletionSource<EnterZoneResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = 0;
        var phase = 0;
        var createdFlag = 0;
        ulong createdAccount = 0;

        _loop.Post(() =>
        {
            Volatile.Write(ref phase, 1);
            try
            {
                if (Volatile.Read(ref outcome) == 2)
                {
                    done.TrySetResult(Unavailable());
                    return;
                }

                LoopEnter entered;
                try
                {
                    entered = EnterOnLoop(command);
                }
                catch (Exception ex)
                {
                    GameLogger.Error("ContentZone", "EnterZone 실패", ex);
                    entered = new LoopEnter(Unavailable(), false, 0);
                }

                var result = entered.Result;
                if (entered.Created)
                {
                    Volatile.Write(ref createdAccount, entered.AccountId);
                    Volatile.Write(ref createdFlag, 1);
                    if (Interlocked.CompareExchange(ref outcome, 1, 0) != 0)
                    {
                        Despawn(entered.AccountId, enqueueSnapshot: false);
                        result = Unavailable();
                    }
                }

                done.TrySetResult(result);
            }
            finally
            {
                Volatile.Write(ref phase, 2);
                finished.TrySetResult();
            }
        });

        try
        {
            return await done.Task.WaitAsync(_enterTimeout);
        }
        catch (TimeoutException)
        {
            if (Interlocked.CompareExchange(ref outcome, 2, 0) != 0)
            {
                if (Volatile.Read(ref createdFlag) == 1)
                    await RollbackSpawnAsync(createdAccount);

                return Unavailable();
            }

            if (Volatile.Read(ref phase) != 0)
            {
                try
                {
                    await finished.Task.WaitAsync(_enterTimeout);
                }
                catch (TimeoutException)
                {
                    GameLogger.Warn("ContentZone", "입장 취소가 존 루프에서 끝나지 않았습니다.");
                }
            }

            return Unavailable();
        }
    }

    public void LeaveZone(ulong accountId)
    {
        if (!IsOpen)
            return;

        _loop.Post(() => Despawn(accountId, enqueueSnapshot: true));
    }

    public void BeginDisconnect(ulong accountId, ulong characterId, Action snapshotQueued)
    {
        if (!IsOpen)
        {
            snapshotQueued();
            return;
        }

        var epoch = EpochOf(accountId);
        _loop.Post(() => DisconnectOnLoop(accountId, characterId, snapshotQueued, epoch));
    }

    public void MarkSessionRemoved(ulong accountId)
        => _epoch.AddOrUpdate(accountId, 1, static (_, value) => value + 1);

    private LoopEnter EnterOnLoop(EnterZoneCommand command)
    {
        if (!IsOpen)
            return new LoopEnter(Unavailable(), false, 0);

        if (!_assignments.TryGet(command.SessionToken, out var assignment))
            return new LoopEnter(new EnterZoneResult(EnterZoneStatus.RejectedInvalidToken, null), false, 0);

        if (assignment.CharacterId != command.CharacterId
            || assignment.WorldId != command.WorldId
            || assignment.ZoneId != command.ZoneId)
        {
            return new LoopEnter(new EnterZoneResult(EnterZoneStatus.RejectedAssignmentMismatch, null), false, 0);
        }

        if (command.CanSpawn != null && !command.CanSpawn())
            return new LoopEnter(Unavailable(), false, 0);

        var epoch = EpochOf(assignment.AccountId);
        if (_occupants.TryGetValue(assignment.AccountId, out var existing))
        {
            if (existing.Epoch == epoch)
                return new LoopEnter(new EnterZoneResult(EnterZoneStatus.Spawned, existing.EntityId), false, assignment.AccountId);

            Despawn(assignment.AccountId, enqueueSnapshot: false);
        }

        var entityId = Interlocked.Increment(ref _nextEntityId);
        var occupant = new Occupant
        {
            EntityId = entityId,
            AccountId = assignment.AccountId,
            CharacterId = assignment.CharacterId,
            Epoch = epoch,
            Pose = assignment.EnterPose,
            ReadSnapshot = command.ReadSnapshot,
            ConsumeDirty = command.ConsumeDirty,
            Deliver = command.Deliver ?? (_ => { }),
        };

        _occupants.Add(assignment.AccountId, occupant);
        _spawned[assignment.AccountId] = 0;
        _aoi.Subscribe(entityId, occupant.Deliver);

        foreach (var other in _occupants.Values)
            occupant.Deliver(new ZoneEvent(ZoneEventKind.Snapshot, other.EntityId));

        _aoi.Publish(new ZoneEvent(ZoneEventKind.Entered, entityId));
        return new LoopEnter(new EnterZoneResult(EnterZoneStatus.Spawned, entityId), true, assignment.AccountId);
    }

    private void DisconnectOnLoop(ulong accountId, ulong characterId, Action snapshotQueued, int epoch)
    {
        try
        {
            if (EpochOf(accountId) != epoch)
            {
                DropStale(accountId, epoch);
                return;
            }

            if (_occupants.TryGetValue(accountId, out var occupant) && occupant.CharacterId != characterId)
            {
                GameLogger.Warn("ContentZone",
                    $"BeginDisconnect 캐릭터 불일치 (AccountId={accountId}, CharacterId={characterId})");
                return;
            }

            Despawn(accountId, enqueueSnapshot: true);
            _assignments.Revoke(accountId);
        }
        catch (Exception ex)
        {
            GameLogger.Error("ContentZone", $"BeginDisconnect 처리 실패 (AccountId={accountId})", ex);
        }
        finally
        {
            if (EpochOf(accountId) == epoch)
            {
                try
                {
                    snapshotQueued();
                }
                catch (Exception ex)
                {
                    GameLogger.Error("ContentZone", $"SnapshotQueued 통지 실패 (AccountId={accountId})", ex);
                }
            }
        }
    }

    private void Despawn(ulong accountId, bool enqueueSnapshot)
    {
        if (!_occupants.Remove(accountId, out var occupant))
            return;

        _spawned.TryRemove(accountId, out _);
        if (enqueueSnapshot && occupant.Epoch == EpochOf(accountId))
        {
            try
            {
                EnqueueRow(occupant.ReadSnapshot);
            }
            catch (Exception ex)
            {
                GameLogger.Error("ContentZone",
                    $"퇴장 스냅샷 적재 실패 (AccountId={accountId})", ex);
            }
        }

        _aoi.Unsubscribe(occupant.EntityId);
        _aoi.Publish(new ZoneEvent(ZoneEventKind.Left, occupant.EntityId));
    }

    private void DropStale(ulong accountId, int epoch)
    {
        if (!_occupants.TryGetValue(accountId, out var occupant) || occupant.Epoch != epoch)
            return;

        Despawn(accountId, enqueueSnapshot: false);
    }

    private async Task RollbackSpawnAsync(ulong accountId)
    {
        var undone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Post(() =>
        {
            Despawn(accountId, enqueueSnapshot: false);
            undone.TrySetResult();
        });

        try
        {
            await undone.Task.WaitAsync(_enterTimeout);
        }
        catch (TimeoutException)
        {
            GameLogger.Warn("ContentZone",
                $"입장 실패 후 스폰 회수가 끝나지 않았습니다 (AccountId={accountId})");
        }
    }

    private int EpochOf(ulong accountId) => _epoch.GetValueOrDefault(accountId);

    private void OnTick(float dtSeconds)
    {
        if (_snapshotIntervalSeconds > 0f)
        {
            _snapshotAcc += dtSeconds;
            if (_snapshotAcc < _snapshotIntervalSeconds)
                return;
            _snapshotAcc = 0f;
        }

        foreach (var occupant in _occupants.Values)
        {
            if (occupant.Epoch != EpochOf(occupant.AccountId))
                continue;

            try
            {
                EnqueueRow(occupant.ConsumeDirty);
            }
            catch (Exception ex)
            {
                GameLogger.Error("ContentZone",
                    $"주기 스냅샷 적재 실패 (AccountId={occupant.AccountId})", ex);
            }
        }
    }

    private void EnqueueRow(Func<CharacterRow?>? read)
    {
        if (read == null)
            return;

        var row = read();
        if (row != null)
            _snapshots.Enqueue(row);
    }

    private static EnterZoneResult Unavailable()
        => new(EnterZoneStatus.RejectedZoneUnavailable, null);

    private readonly record struct LoopEnter(EnterZoneResult Result, bool Created, ulong AccountId);

    private sealed class Occupant
    {
        public long EntityId { get; init; }
        public ulong AccountId { get; init; }
        public ulong CharacterId { get; init; }
        public int Epoch { get; init; }
        public ZonePose Pose { get; init; }
        public Func<CharacterRow?>? ReadSnapshot { get; init; }
        public Func<CharacterRow?>? ConsumeDirty { get; init; }
        public Action<ZoneEvent> Deliver { get; init; } = _ => { };
    }
}
