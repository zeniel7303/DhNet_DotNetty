# 세션 및 연결 관리
# Session & Connection Management

## GameSession 구조

`IChannel`을 래핑하여 게임 로직과 네트워크 레이어를 분리합니다.

```csharp
public class GameSession
{
    public IChannel Channel { get; }
    public Player? Player { get; set; }

    public GameSession(IChannel channel) => Channel = channel;

    public Task SendAsync(GamePacket packet) =>
        Channel.WriteAndFlushAsync(packet);
}
```

## 세션 생명주기

```csharp
// ChannelActive: 연결 시 세션 등록
public override void ChannelActive(IChannelHandlerContext ctx)
{
    _session = new GameSession(ctx.Channel);
    GameSessionSystem.Instance.Register(_session);
    Console.WriteLine($"[연결] {ctx.Channel.RemoteAddress}");
}

// ChannelInactive: 해제 시 세션 제거 + 플레이어 정리
public override void ChannelInactive(IChannelHandlerContext ctx)
{
    _session?.Player?.DisConnect();    // 로비/룸 퇴장 먼저
    if (_session != null)
        GameSessionSystem.Instance.Unregister(_session);
    Console.WriteLine($"[해제] {ctx.Channel.RemoteAddress}");
    _session = null;
}
```

## GameSessionSystem 조회

```csharp
// IChannelId로 세션 찾기
if (GameSessionSystem.Instance.TryGet(ctx.Channel.Id, out var session))
    PacketRouter.Dispatch(session, packet);
```

## 패킷 전송

```csharp
// fire-and-forget: 응답 전송
_ = session.SendAsync(new GamePacket { ResLogin = new ResLogin { PlayerId = id } });

// await 필요 시
await session.Channel.WriteAndFlushAsync(packet);
```

## 베스트 프랙티스

### ✅ Do
- `_session?.Player?.DisConnect()` null 조건 연산자 사용
- `WriteAndFlushAsync` 사용 (Write + Flush 원자적 처리)
- ChannelInactive 마지막에 `_session = null` 초기화

### ❌ Don't
- 핸들러 외부에서 `IChannel` 직접 보관 (GameSession 통해 사용)
- 세션 등록 없이 Player에 접근
- `ctx.CloseAsync()` 를 ChannelInactive 핸들러에서 중복 호출
