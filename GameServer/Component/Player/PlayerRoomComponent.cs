using Common.Server.Component;
using GameServer.Component.Room;
using GameServer.Protocol;

namespace GameServer.Component.Player;

// PlayerComponent가 소유하는 서브 컴포넌트 — WorkerSystem에 등록되지 않는다.
// 모든 메서드는 PlayerComponent 워커 스레드에서 직렬 호출되므로 EnqueueEvent 불필요.
// CurrentRoom 쓰기 경로:
//   - RoomComponent.Enter(player) → PlayerLobbyComponent.RoomEnter → 워커 스레드
//   - RoomComponent.Leave(player, _) → PlayerRoomComponent.Exit/Disconnect → 워커 스레드
// → 모든 쓰기가 PlayerComponent 워커 스레드 → volatile 불필요
public class PlayerRoomComponent(PlayerComponent player) : BaseComponent
{
    public RoomComponent? CurrentRoom { get; internal set; }

    public override void Initialize() { }

    public void Chat(ReqRoomChat req)
    {
        var room = CurrentRoom;
        if (room == null) return;
        if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 500) return;

        room.Chat(player, req.Message);
    }

    public void Ready()
    {
        CurrentRoom?.Ready(player);
    }

    // ReqRoomExit — 정상 퇴장 (isDisconnect=false, ResRoomExit 전송됨)
    public void Exit()
    {
        CurrentRoom?.Leave(player, false);
    }

    // PlayerComponent.DisconnectAsync()에서 호출 — isDisconnect=true, ResRoomExit 미전송
    public void Disconnect()
    {
        CurrentRoom?.Leave(player, true);
    }

    protected override void OnDispose() { }
}
