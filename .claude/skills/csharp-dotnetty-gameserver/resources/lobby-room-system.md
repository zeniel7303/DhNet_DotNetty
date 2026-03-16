# 로비/룸 시스템 설계
# Lobby & Room System Design

## 아키텍처 개요

```
플레이어 연결
  └─→ LoginController → Lobby.Enter(player)

Lobby (Channel<Action> 기반)
  ├─→ Enter(player)   : 추가 + NotiLobbyEnter 브로드캐스트
  ├─→ Leave(player)   : 제거
  ├─→ Chat(player, msg): NotiLobbyChat 브로드캐스트
  └─→ [FlushAsync 루프] 작업 순차 처리

LobbySystem (싱글톤)
  ├─→ Lobby: 단일 로비 인스턴스
  ├─→ GetOrCreateRoom(): 빈 방 찾기 or 새 방 생성
  └─→ RemoveRoom(roomId): 빈 방 정리

Room (Channel<Action> 기반, 최대 2인)
  ├─→ Enter(player)              : 추가 + NotiRoomEnter 브로드캐스트
  ├─→ Leave(player, isDisconnect): 제거 + NotiRoomExit + 빈 방 정리
  ├─→ Chat(player, msg)          : NotiRoomChat 브로드캐스트
  └─→ IsFull: _players.Count >= MaxPlayers
```

## Job Queue 패턴 (Channel<Action>)

```csharp
private readonly System.Threading.Channels.Channel<Action> _jobChannel =
    System.Threading.Channels.Channel.CreateUnbounded<Action>();

public void EnqueueJob(Action job) => _jobChannel.Writer.TryWrite(job);

private async Task FlushAsync()
{
    await foreach (var job in _jobChannel.Reader.ReadAllAsync())
        job();  // 순차 실행 — 락 불필요
}
// 생성자: _ = Task.Run(FlushAsync);
```

## 브로드캐스트 패턴

```csharp
// Lobby: 모든 참가자에게 전송
private void BroadcastToLobby(GamePacket packet)
{
    foreach (var p in _players.Values)
        _ = p.Session.SendAsync(packet);
}

// Room: 룸 내 모든 참가자에게 전송
private void BroadcastToRoom(GamePacket packet)
{
    foreach (var p in _players)
        _ = p.Session.SendAsync(packet);
}
```

## 룸 입장/퇴장 흐름

```csharp
// 룸 입장: Lobby → Room (LobbyController)
var room = LobbySystem.Instance.GetOrCreateRoom();
LobbySystem.Instance.Lobby.Leave(player);  // 로비에서 먼저 나가기
room.Enter(player);                         // 룸에 입장

// 룸 퇴장: Room → Lobby 복귀 (RoomController)
player.CurrentRoom.Leave(player, isDisconnect: false);
LobbySystem.Instance.Lobby.Enter(player);  // 로비로 복귀

// 비정상 종료: Room → 로비 복귀 없음 (Player.DisConnect)
player.CurrentRoom.Leave(player, isDisconnect: true);
```

## GetOrCreateRoom 패턴

```csharp
public Room GetOrCreateRoom()
{
    foreach (var room in _rooms.Values)
        if (!room.IsFull) return room;  // 빈 방 재활용

    var newRoom = new Room();
    _rooms.TryAdd(newRoom.Id, newRoom);
    return newRoom;
}
```

## 베스트 프랙티스

### ✅ Do
- Lobby/Room 상태 변경은 `EnqueueJob`으로 직렬화
- Leave 전에 null 체크: `if (player?.CurrentRoom == null) return;`
- 방이 비면 `LobbySystem.RemoveRoom(roomId)` 호출

### ❌ Don't
- Lobby/Room 내부 컬렉션을 외부에서 직접 수정
- Enter/Leave를 잡큐 없이 직접 호출
