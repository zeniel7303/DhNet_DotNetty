# 플레이어 시스템 관리
# Player System Management

## Player 엔티티 구조

```csharp
public class Player
{
    public ulong Id { get; }               // UniqueIdGenerator로 생성
    public string Name { get; }            // 로그인 시 설정
    public GameSession Session { get; }    // 네트워크 세션 참조
    public Room? CurrentRoom { get; set; } // null = 로비 상태

    public Player(GameSession session, string name)
    {
        Id = UniqueIdGenerator.Instance.Generate();
        Name = name;
        Session = session;
    }
}
```

## 플레이어 상태 추적

| `CurrentRoom` 값 | 상태 | 위치 |
|----------------|------|------|
| `null` | 로비 | `Lobby._players`에 있음 |
| Room 인스턴스 | 룸 참가 중 | `Room._players`에 있음 |

## PlayerSystem 싱글톤

```csharp
// 로그인 시 등록 (LoginController)
var player = new Player(session, req.PlayerName);
session.Player = player;
PlayerSystem.Instance.Add(player);
LobbySystem.Instance.Lobby.Enter(player);

// 조회 (PlayerId로)
if (PlayerSystem.Instance.TryGet(playerId, out var player))
    Use(player);

// 제거 (DisConnect 시)
PlayerSystem.Instance.Remove(player.Id);
```

## DisConnect() 생명주기

ChannelInactive → `_session?.Player?.DisConnect()` 자동 호출.

```csharp
public void DisConnect()
{
    if (CurrentRoom != null)
    {
        // isDisconnect=true: 로비 복귀 없이 룸에서만 퇴장
        CurrentRoom.Leave(this, isDisconnect: true);
        CurrentRoom = null;
    }
    else
    {
        LobbySystem.Instance.Lobby.Leave(this);
    }
}
```

## UniqueId 생성

```csharp
// Interlocked.Increment: 원자적 증가, 별도 락 불필요
private long _counter = 0;
public ulong Generate() => (ulong)Interlocked.Increment(ref _counter);
```

## 베스트 프랙티스

### ✅ Do
- Player 생성은 LoginController에서만 (중복 로그인 방지)
- `session.Player = player` 설정 후 `PlayerSystem.Instance.Add(player)` 순서 유지
- DisConnect 호출 전 `session.Player != null` 확인

### ❌ Don't
- Player 없이 세션에 접근 (항상 `session.Player?.Name` 패턴 사용)
- 동일 세션에 Player 두 번 설정
- UniqueIdGenerator 없이 Id 직접 할당
