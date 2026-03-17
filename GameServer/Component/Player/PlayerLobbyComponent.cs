using Common.Server.Component;
using GameServer.Component.Lobby;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Component.Player;

// PlayerComponent가 소유하는 서브 컴포넌트 — WorkerSystem에 등록되지 않는다.
// 모든 메서드는 PlayerComponent 워커 스레드에서 직렬 호출되므로 EnqueueEvent 불필요.
// CurrentLobby 쓰기 경로:
//   - LoginController → player.EnqueueEvent(() => lobby.TryEnter(player)) → 워커 틱
//   - RoomEnter, Disconnect → 워커 스레드 직접 호출
// → 모든 쓰기가 PlayerComponent 워커 스레드 → volatile 불필요
public class PlayerLobbyComponent(PlayerComponent player) : BaseComponent
{
    public LobbyComponent? CurrentLobby { get; internal set; }

    public override void Initialize() { }

    // ReqLobbyList — 로비 목록 조회
    public void LobbyList(ReqLobbyList req)
    {
        var res = new ResLobbyList();
        foreach (var info in LobbySystem.Instance.GetLobbyList())
        {
            res.Lobbies.Add(info);
        }
        _ = player.Session.SendAsync(new GamePacket { ResLobbyList = res });
    }

    // ReqLobbyChat — 로비 채팅
    public void Chat(ReqLobbyChat req)
    {
        var lobby = CurrentLobby;
        if (lobby == null || player.Room.CurrentRoom != null) return;
        if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 500) return;

        lobby.Chat(player, req.Message);
    }

    // ReqRoomEnter — 로비에서 룸 입장
    public void RoomEnter(ReqRoomEnter req)
    {
        var lobby = CurrentLobby;
        if (player.Room.CurrentRoom != null || lobby == null)
        {
            _ = player.Session.SendAsync(new GamePacket { ResRoomEnter = new ResRoomEnter { Success = false } });
            return;
        }

        // 이 메서드는 PlayerComponent 워커 스레드에서 직렬 호출된다 (동일 player는 항상 동일 워커).
        // lobby.Leave → CurrentLobby = null, newRoom.Enter → CurrentRoom = this
        // 모두 같은 워커 스레드에서 동기적으로 실행되므로 스레드 안전.
        var newRoom = lobby.GetOrCreateRoom();
        lobby.Leave(player);
        newRoom.Enter(player);
    }

    // PlayerComponent.DisconnectAsync()에서 호출 — 로비 미입장 시 무시
    public void Disconnect()
    {
        CurrentLobby?.Leave(player);
    }

    protected override void OnDispose() { }
}
