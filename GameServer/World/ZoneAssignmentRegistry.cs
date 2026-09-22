using System.Collections.Concurrent;

namespace GameServer.World;

/// <summary>Session이 발급한 AssignZone 배정. 존 루프와 로그인 스레드가 함께 읽는다.</summary>
public sealed class ZoneAssignmentRegistry
{
    private readonly ConcurrentDictionary<string, ZoneAssignment> _byToken = new();
    private readonly ConcurrentDictionary<ulong, ZoneAssignment> _byAccount = new();

    public ZoneAssignment Assign(ulong accountId, ulong characterId, string worldId, string zoneId, ZonePose pose)
    {
        var assignment = new ZoneAssignment(
            Guid.NewGuid().ToString("N"),
            accountId,
            characterId,
            worldId,
            zoneId,
            pose);

        _byToken[assignment.SessionToken] = assignment;
        _byAccount[accountId] = assignment;
        return assignment;
    }

    public bool TryGet(string sessionToken, out ZoneAssignment assignment)
        => _byToken.TryGetValue(sessionToken, out assignment);

    public void Revoke(ulong accountId)
    {
        if (_byAccount.TryRemove(accountId, out var assignment))
            _byToken.TryRemove(assignment.SessionToken, out _);
    }
}
