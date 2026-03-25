using GameServer.Component.Lobby;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Player;

// PlayerComponent가 소유하는 서브 컴포넌트 — WorkerSystem에 등록되지 않는다.
// 모든 메서드는 PlayerComponent 워커 스레드에서 직렬 호출되므로 별도 동기화 불필요.
// CurrentLobby 쓰기 경로:
//   - LoginController → player.EnqueueEvent(() => lobby.TryEnter(player)) → 워커 틱
//   - CreateRoom/RoomEnter/Disconnect → 워커 스레드 직접 호출
// → 모든 쓰기가 PlayerComponent 워커 스레드 → volatile 불필요
public class PlayerLobbyComponent(PlayerComponent player)
{
    public LobbyComponent? CurrentLobby { get; internal set; }

    // 현재 로비의 룸 목록 조회
    public void RoomList(ReqRoomList req)
    {
        var res = new ResRoomList();
        var lobby = CurrentLobby;
        if (lobby != null)
        {
            foreach (var info in lobby.GetRoomList())
                res.Rooms.Add(info);
        }
        _ = player.Session.SendAsync(new GamePacket { ResRoomList = res });
    }

    // 새 룸 생성 후 자동 입장 — 응답은 ResRoomEnter로 통합
    public void CreateRoom(ReqCreateRoom req)
    {
        if (player.Room.CurrentRoom != null)
        {
            _ = player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { ErrorCode = ErrorCode.AlreadyInRoom } });
            return;
        }

        var lobby = CurrentLobby;
        if (lobby == null)
        {
            _ = player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { ErrorCode = ErrorCode.NotInLobby } });
            return;
        }

        var newRoom = lobby.CreateRoom();
        if (newRoom == null) return; // 방어적 처리 — 로그는 LobbyComponent에서 출력됨

        lobby.Leave(player);
        newRoom.Enter(player);
    }

    // 특정 room_id의 룸에 입장
    public void RoomEnter(ReqRoomEnter req)
    {
        if (player.Room.CurrentRoom != null)
        {
            _ = player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { ErrorCode = ErrorCode.AlreadyInRoom } });
            return;
        }

        var room = LobbySystem.Instance.TryGetRoom(req.RoomId);
        if (room == null || room.IsGameStarted)
        {
            _ = player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { ErrorCode = ErrorCode.RoomNotFound } });
            return;
        }

        if (!room.TryReserve())
        {
            _ = player.Session.SendAsync(new GamePacket
                { ResRoomEnter = new ResRoomEnter { ErrorCode = ErrorCode.RoomFull } });
            return;
        }

        CurrentLobby?.Leave(player);
        room.Enter(player);
    }

    // PlayerComponent.DisconnectAsync()에서 호출 — 로비 미입장 시 무시
    public void Disconnect()
    {
        CurrentLobby?.Leave(player);
    }
}
