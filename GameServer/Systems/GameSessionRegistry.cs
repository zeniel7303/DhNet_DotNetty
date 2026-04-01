using System.Collections.Concurrent;
using GameServer.Component.Stage;

namespace GameServer.Systems;

/// <summary>
/// 활성 게임 세션 레지스트리. 세션 조회 및 통계 제공.
/// 실제 틱은 RoomSystem(WorkerSystem)이 RoomComponent.Update(dt) → StageComponent.Update(dt) 순으로 구동한다.
/// </summary>
public class GameSessionRegistry
{
    public static readonly GameSessionRegistry Instance = new();

    private readonly ConcurrentDictionary<ulong, StageComponent> _sessions = new();

    private GameSessionRegistry() { }

    public void Register(StageComponent session)
        => _sessions.TryAdd(session.RoomId, session);

    public void Unregister(ulong roomId)
        => _sessions.TryRemove(roomId, out _);

    public int ActiveSessionCount => _sessions.Count;
}
