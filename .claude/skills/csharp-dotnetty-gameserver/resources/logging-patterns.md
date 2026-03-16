# 로깅 패턴
# Logging Patterns

## 현재 프로젝트 로그 형식

`Console.WriteLine` 기반, `[태그]` 접두사 규칙을 사용합니다.

```csharp
// 연결/해제
Console.WriteLine($"[연결] {ctx.Channel.RemoteAddress}");
Console.WriteLine($"[해제] {ctx.Channel.RemoteAddress}");

// 로그인
Console.WriteLine($"[Login] 로그인 성공: {player.Name} (Id={player.Id})");
Console.WriteLine($"[Login] 중복 로그인 시도: {session.Player.Name}");

// 로비/룸 이벤트
Console.WriteLine($"[Lobby] {player.Name}이 입장했습니다.");
Console.WriteLine($"[Lobby] 채팅: {player.Name}: {message}");
Console.WriteLine($"[Room:{roomId}] {player.Name} 입장 ({_players.Count}/{MaxPlayers})");
Console.WriteLine($"[Room:{roomId}] {player.Name} 퇴장");

// 경고/오류
Console.WriteLine($"[예외] {ex.Message}");
Console.WriteLine($"[PacketRouter] 미처리 패킷: {packet.PayloadCase}");
```

## 로그 태그 규칙

| 태그 | 용도 |
|------|------|
| `[연결]` / `[해제]` | 클라이언트 TCP 연결/해제 |
| `[Login]` | 로그인 처리 |
| `[Lobby]` | 로비 이벤트 |
| `[Room:N]` | 룸 이벤트 (N=룸 ID) |
| `[예외]` | 예외 발생 |
| `[PacketRouter]` | 미처리 패킷 경고 |
| `[클라이언트]` | GameClient 측 이벤트 |

## 클라이언트 측 로그 패턴

```csharp
// 서버 응답 수신 출력
Console.WriteLine($"[ResLogin] PlayerId={res.PlayerId}, Name={res.PlayerName}");
Console.WriteLine($"[NotiLobbyChat] {noti.PlayerName}: {noti.Message}");
Console.WriteLine($"[NotiRoomEnter] {noti.PlayerName}이 룸에 입장했습니다.");
Console.WriteLine($"[NotiRoomChat] {noti.PlayerName}: {noti.Message}");
Console.WriteLine($"[NotiRoomExit] {noti.PlayerName}이 룸을 떠났습니다.");
```

## 베스트 프랙티스

### ✅ Do
- 충분한 컨텍스트 포함 (이름, ID, 상태 등)
- 구조화된 태그로 grep 가능하게

### ❌ Don't
- `ChannelRead0`에서 모든 패킷 로깅 (EventLoop 부하)
- 컨텍스트 없는 단순 `"error"` 로그
