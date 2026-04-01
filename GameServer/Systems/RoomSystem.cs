using Common.Server.Component;
using GameServer.Component.Room;

namespace GameServer.Systems;

/// <summary>
/// RoomComponent의 틱 스레드를 제공하는 시스템.
/// 구동 체인: RoomSystem → RoomComponent.Update(dt) → StageComponent.Update(dt)
///
/// 설계 결정 — Dispose 책임:
///   RoomSystem은 워커 스레드 관리만 담당하며, RoomComponent의 Dispose 책임은 LobbyComponent가 갖는다.
///   이유:
///     1. _rooms 레지스트리를 소유한 쪽이 생명주기도 책임진다 (PlayerSystem과 동일 원칙).
///     2. RoomSystem이 _rooms를 가지면 GetByLobby()가 O(n) 전체 스캔이 되어 룸 수 증가 시 불리하다.
///     3. 워커 스케일아웃은 workerCount만 늘리면 되며 구조 변경이 없다.
/// </summary>
public class RoomSystem
{
    public static readonly RoomSystem Instance = new();

    private readonly WorkerSystem<RoomComponent> _workers = new(workerCount: 1, intervalMs: 100);

    // WorkerSystem.Add()가 room.Initialize()를 자동 호출한다.
    public void Add(RoomComponent room) => _workers.Add(room);

    // 워커에서만 제거한다. Dispose는 호출자(LobbyComponent) 책임.
    public void Remove(RoomComponent room) => _workers.Remove(room);

    public void StartSystem() => _workers.StartSystem();
    public void Stop()        => _workers.Stop();
}
