# 에러 핸들링 전략
# Error Handling Strategy

## ExceptionCaught 패턴

```csharp
public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
{
    Console.WriteLine($"[예외] {ex.Message}");
    ctx.CloseAsync();  // ChannelInactive 트리거 → 세션 정리 자동 처리
}
```

## GamePacket PayloadCase 검증

ProtobufDecoder가 파싱한 패킷은 반드시 `PayloadCase` 확인이 필요합니다.

```csharp
protected override void ChannelRead0(IChannelHandlerContext ctx, GamePacket packet)
{
    if (_session == null) return;

    if (packet.PayloadCase == GamePacket.PayloadOneofCase.None)
    {
        Console.WriteLine("[경고] 빈 패킷 수신 — 무시");
        return;
    }

    PacketRouter.Dispatch(_session, packet);
}
```

## PacketRouter 미처리 패킷

```csharp
default:
    Console.WriteLine($"[PacketRouter] 미처리 패킷: {packet.PayloadCase}");
    break;
```

## 비동기 fire-and-forget 패턴

```csharp
// ✅ _ = 할당으로 컴파일러 경고 억제
_ = session.SendAsync(responsePacket);

// ✅ 에러 로깅이 필요한 경우
_ = session.SendAsync(packet).ContinueWith(t =>
{
    if (t.IsFaulted)
        Console.WriteLine($"[전송 오류] {t.Exception?.GetBaseException().Message}");
}, TaskContinuationOptions.OnlyOnFaulted);

// ❌ Task를 그냥 무시 (컴파일러 경고 + 예외 손실)
session.SendAsync(responsePacket);
```

## Controller null 가드 패턴

```csharp
public static void HandleChat(GameSession session, ReqLobbyChat req)
{
    var player = session.Player;
    if (player == null || player.CurrentRoom != null) return;  // null 가드 먼저
    LobbySystem.Instance.Lobby.Chat(player, req.Message);
}
```

## Player.DisConnect() 안전 패턴

```csharp
public void DisConnect()
{
    if (CurrentRoom != null)
    {
        CurrentRoom.Leave(this, isDisconnect: true);  // 로비 복귀 없음
        CurrentRoom = null;
    }
    else
    {
        LobbySystem.Instance.Lobby.Leave(this);
    }
}
```

## 베스트 프랙티스

### ✅ Do
- 모든 핸들러에 `ExceptionCaught` 구현
- `ctx.CloseAsync()` 호출로 ChannelInactive 통해 세션 정리
- Controller에서 null 가드 먼저

### ❌ Don't
- 예외를 삼키고 계속 진행 (세션 상태가 망가짐)
- `CloseAsync()` 없이 예외 무시
- Controller에서 Player null 체크 생략
