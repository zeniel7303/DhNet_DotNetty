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
    private readonly float _snapshotIntervalSeconds;
    private float _snapshotAcc;
    private long _nextEntityId;
    private int _open;
    private string _worldId = "main";
    private string _zoneId = "room-map";
    private TimeSpan _enterTimeout = TimeSpan.FromSeconds(3);

    public ContentZone(IZoneSnapshotWriter snapshots, float snapshotIntervalSeconds = 60f)
    {
        _snapshots = snapshots;
        _snapshotIntervalSeconds = snapshotIntervalSeconds;
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
                done.TrySetResult(EnterOnLoop(command));
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
        var cancelled = 0;
        _loop.Post(() =>
        {
            if (Volatile.Read(ref cancelled) == 1)
            {
                done.TrySetResult(Unavailable());
                return;
            }

            EnterZoneResult result;
            try
            {
                result = EnterOnLoop(command);
            }
            catch (Exception ex)
            {
                GameLogger.Error("ContentZone", "EnterZone 실패", ex);
                result = Unavailable();
            }

            done.TrySetResult(result);
        });

        try
        {
            return await done.Task.WaitAsync(_enterTimeout);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref cancelled, 1);
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

        _loop.Post(() => DisconnectOnLoop(accountId, characterId, snapshotQueued));
    }

    private EnterZoneResult EnterOnLoop(EnterZoneCommand command)
    {
        if (!IsOpen)
            return Unavailable();

        if (!_assignments.TryGet(command.SessionToken, out var assignment))
            return new EnterZoneResult(EnterZoneStatus.RejectedInvalidToken, null);

        if (assignment.CharacterId != command.CharacterId
            || assignment.WorldId != command.WorldId
            || assignment.ZoneId != command.ZoneId)
        {
            return new EnterZoneResult(EnterZoneStatus.RejectedAssignmentMismatch, null);
        }

        if (command.CanSpawn != null && !command.CanSpawn())
            return Unavailable();

        if (_occupants.TryGetValue(assignment.AccountId, out var existing))
            return new EnterZoneResult(EnterZoneStatus.Spawned, existing.EntityId);

        var entityId = Interlocked.Increment(ref _nextEntityId);
        var occupant = new Occupant
        {
            EntityId = entityId,
            AccountId = assignment.AccountId,
            CharacterId = assignment.CharacterId,
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
        return new EnterZoneResult(EnterZoneStatus.Spawned, entityId);
    }

    private void DisconnectOnLoop(ulong accountId, ulong characterId, Action snapshotQueued)
    {
        try
        {
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

    private void Despawn(ulong accountId, bool enqueueSnapshot)
    {
        if (!_occupants.Remove(accountId, out var occupant))
            return;

        _spawned.TryRemove(accountId, out _);
        if (enqueueSnapshot)
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

    private sealed class Occupant
    {
        public long EntityId { get; init; }
        public ulong AccountId { get; init; }
        public ulong CharacterId { get; init; }
        public ZonePose Pose { get; init; }
        public Func<CharacterRow?>? ReadSnapshot { get; init; }
        public Func<CharacterRow?>? ConsumeDirty { get; init; }
        public Action<ZoneEvent> Deliver { get; init; } = _ => { };
    }
}
