using GameServer.Database.Rows;

namespace GameServer.World;

public readonly record struct ZonePose(float X, float Y, float Z, float Yaw);

/// <summary>
/// Session이 발급한 입장 배정.
/// CharacterId는 별도 스키마 없이 기존 account_id를 재사용한다.
/// </summary>
public readonly record struct ZoneAssignment(
    string SessionToken,
    ulong AccountId,
    ulong CharacterId,
    string WorldId,
    string ZoneId,
    ZonePose EnterPose);

public enum EnterZoneStatus
{
    Spawned,
    RejectedInvalidToken,
    RejectedAssignmentMismatch,
    RejectedZoneUnavailable,
}

public readonly record struct EnterZoneResult(EnterZoneStatus Status, long? EntityId);

public enum ZoneEventKind
{
    Snapshot,
    Entered,
    Left,
}

public readonly record struct ZoneEvent(ZoneEventKind Kind, long EntityId);

public sealed class EnterZoneCommand
{
    public required string SessionToken { get; init; }
    public required ulong CharacterId { get; init; }
    public required string WorldId { get; init; }
    public required string ZoneId { get; init; }
    public Func<bool>? CanSpawn { get; set; }
    public Func<CharacterRow?>? ReadSnapshot { get; set; }
    public Func<CharacterRow?>? ConsumeDirty { get; set; }
    public Action<ZoneEvent>? Deliver { get; set; }
}

/// <summary>
/// 존이 캐릭터 스냅샷을 적재하는 계약. 완료를 돌려주지 않으므로 틱이 DB를 기다릴 수 없다.
/// FlushAccount는 이 계약에 없다.
/// </summary>
public interface IZoneSnapshotWriter
{
    void Enqueue(CharacterRow row);
}

/// <summary>World가 스냅샷을 넣은 뒤 Session에 알리는 포트. Flush는 호출하지 않는다.</summary>
public interface IZoneDisconnect
{
    void BeginDisconnect(ulong accountId, ulong characterId, Action snapshotQueued);

    /// <summary>
    /// Session.Remove가 끝난 계정. 이후의 Despawn·Upsert·SnapshotQueued는 무시한다.
    /// </summary>
    void MarkSessionRemoved(ulong accountId);
}
