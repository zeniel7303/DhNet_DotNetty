# 코드 리뷰 — 2026-03-23

> Opus 심층 코드 리뷰. 동시성 정확성, 게임 로직, 에러 처리, 셧다운 흐름을 실제 코드 경로 추적 기반으로 분석.

---

## 종합 평가

동시성 설계와 아키텍처 전반이 높은 수준. SessionSystem 단일 스레드 이벤트 처리, WorkerSystem 해싱 기반 워커 분배, RoomComponent CAS 기반 슬롯 관리 등 핵심 설계가 정확하게 구현되어 있으며 대부분의 edge case가 멱등성과 방어적 null 체크로 처리되어 있다.

**특히 잘 된 부분:**
- `RoomComponent.TryReserve` / `TryReleaseAndClose` — lock 없이 CAS 루프로 슬롯 예약/반환 원자화, 마지막 플레이어 퇴장 시 `1 → -1` 전환으로 `TryReserve`와의 race 완전 제거
- `SessionSystem` 이벤트 큐 — 단일 스레드 FIFO 처리로 Disconnect / PlayerGameEnter 순서 경쟁 케이스 2가지 모두 정확히 처리
- `PlayerComponent.DisconnectAsync` — `lock(this)` 범위를 참조 캡처 2줄로 한정, 실제 Disconnect 호출은 락 외부에서 수행하여 교차 락 방지. 코드에 설계 의도 주석 명시
- 셧다운 순서 — `CloseAsync → StopAsync → WaitUntilEmpty → EventLoopGroup` 순서가 정확, DB write 완료 후 EventLoopGroup 종료 보장

---

## 버그 / 즉시 수정 권장

### BUG-1: 인증 전 패킷 큐 무제한 적재 — 메모리 고갈

**위치:** `GameServer/Network/GameServerHandler.cs:48-51`, `GameServer/Network/SessionComponent.cs:19, 34`

#### 문제 코드

```csharp
// GameServerHandler.cs — ChannelRead0
default:
    _session.EnqueuePacket(packet);  // 로그인 전후 구분 없이 무조건 적재
    break;

// SessionComponent.cs
private readonly ConcurrentQueue<GamePacket> _packetQueue = new();  // 크기 제한 없음
```

#### 왜 문제인가

`ReqLogin` / `ReqRegister`를 제외한 모든 패킷이 `_packetQueue`에 적재된다. `ConcurrentQueue`는 크기 제한이 없다.

로그인 완료 전에는 `session.Player == null`이므로 `PlayerComponent.DrainSessionPackets()`가 호출되지 않는다. 즉 **로그인 없이 패킷을 계속 보내면 큐가 영구적으로 증가한다.**

```
공격 시나리오:
  1. TCP 연결
  2. ReqLogin 전송 안 함
  3. 임의 패킷 초당 수만 개 전송
     → _packetQueue 무한 적재 → 힙 급증 → OOM → 서버 다운
```

`IdleStateHandler`는 30초 유휴 감지를 하지만, 패킷을 계속 전송하면 유휴 상태가 아니므로 감지 불가.

#### 수정 방향 1 (권장) — Player == null 시 즉시 Drop

```csharp
// GameServerHandler.cs
default:
    if (_session.Player != null)
        _session.EnqueuePacket(packet);
    break;
```

로그인 전 게임 패킷은 어차피 처리할 수 없으므로 drop이 올바른 동작이다.

#### 수정 방향 2 — 큐 크기 상한 추가

```csharp
// SessionComponent.cs
private const int MaxPacketQueueSize = 200;

public bool EnqueuePacket(GamePacket packet)
{
    if (_packetQueue.Count >= MaxPacketQueueSize) return false;
    _packetQueue.Enqueue(packet);
    return true;
}

// GameServerHandler.cs
default:
    if (!_session.EnqueuePacket(packet))
    {
        GameLogger.Warn("Session", $"패킷 큐 초과 — 연결 종료: {_session.Channel.RemoteAddress}");
        _ = _session.Channel.CloseAsync();
    }
    break;
```

**권장 조합**: 수정 방향 1(인증 전 drop)과 수정 방향 2(크기 상한)를 모두 적용. 인증 전 drop은 악성 클라이언트를, 크기 상한은 워커 과부하 상황을 각각 방어한다.

---

### BUG-2: 동일 계정 동시 다중 로그인 미방지

**위치:** `GameServer/Network/LoginProcessor.cs:14, 22-23`

#### 문제 코드

```csharp
public static async Task ProcessAsync(SessionComponent session, ReqLogin req)
{
    // 같은 세션 내 중복 ReqLogin만 방지 — 다른 소켓에서 같은 계정 로그인은 통과
    if (session.Player != null) { ... return; }

    var account = await AuthenticateAsync(session, req.Username, req.Password);
    // account_id 기준 "이미 로그인 중인지" 체크 없음
    var player = new PlayerComponent(session, account.username);
    ...
}
```

`TrySetLoginStarted()`는 동일 `SessionComponent`에서 `LoginProcessor.ProcessAsync`가 병렬 실행되는 것을 막는다. 하지만 **서로 다른 소켓에서 동일 username으로 동시 로그인하면 둘 다 통과한다.**

```
시나리오:
  소켓A → ReqLogin("alice", "1234")  ─┐ 동시 실행
  소켓B → ReqLogin("alice", "1234")  ─┘

  두 AuthenticateAsync 모두 DB 조회 성공
  두 PlayerComponent 생성 (PlayerId 다름)
  두 players INSERT (서로 다른 player_id)
  두 players가 로비에 입장
  → "alice" 계정이 두 개의 PlayerComponent로 동시 활성화
```

**결과**: 동일 계정의 아이템 복제, 경험치 이중 지급, 상태 불일치 등 게임 로직 오작동 가능.

#### 수정 방향

**1단계: PlayerComponent에 AccountId 보관**

```csharp
// PlayerComponent.cs
public ulong AccountId { get; }

public PlayerComponent(SessionComponent session, string name, ulong accountId)
{
    AccountId = accountId;
    // ...
}
```

**2단계: PlayerSystem에 account_id 인덱스 추가**

```csharp
// PlayerSystem.cs
private readonly ConcurrentDictionary<ulong, PlayerComponent> _playersByAccount = new();

public bool TryAddByAccount(ulong accountId, PlayerComponent player)
    => _playersByAccount.TryAdd(accountId, player);

public void Remove(PlayerComponent player)
{
    _players.TryRemove(player.PlayerId, out _);
    _playersByAccount.TryRemove(player.AccountId, out _);
    _workers.Remove(player);
    player.Dispose();
}
```

**3단계: LoginProcessor에서 중복 체크**

```csharp
// LoginProcessor.cs
var player = new PlayerComponent(session, account.username, account.account_id);

if (!PlayerSystem.Instance.TryAddByAccount(account.account_id, player))
{
    GameLogger.Warn("Login", $"중복 로그인 시도: {account.username}");
    await session.SendAsync(new GamePacket
    {
        ResLogin = new ResLogin { ErrorCode = ErrorCode.AlreadyLoggedIn }
    });
    return;
}
```

> `AlreadyLoggedIn` ErrorCode를 `error_codes.proto`에 추가 필요. 또는 신규 로그인 시 기존 세션을 강제 종료하는 "다른 기기에서 로그인" 정책도 가능.

---

## 안정성 개선 권장

### IMP-1: `_running` 필드 `volatile` 누락

**위치:** `Common.Server/Component/BaseWorker.cs:12`, `GameServer/Systems/SessionSystem.cs:43`

```csharp
private bool _running;  // volatile 없음
```

`Stop()`(다른 스레드)에서 `false`로 설정하고 `Loop()`(워커 스레드)에서 읽는다. .NET CLR(x86/x64 TSO)과 `Thread.Sleep`의 메모리 배리어 효과 덕분에 **실전에서는 문제가 발생하지 않는다.** 그러나 규격상(ECMA-335) 보장되지 않으므로 `volatile` 추가 권장.

```csharp
private volatile bool _running;
```

---

## 심층 분석 — 안전이 확인된 항목

실제 코드 경로를 추적한 결과 안전하다고 판단된 항목.

### `BaseComponent.EnqueueEvent` disposed race window

`_disposed` 체크와 `Enqueue` 사이에 `Dispose()`가 실행될 수 있다는 이론적 race가 존재한다. 그러나:
- `DisconnectAsync` → `PlayerSystem.Remove` → `player.Dispose()`는 WorkerSystem 워커 스레드에서 직렬 실행
- `EnqueueEventAsync`도 같은 워커 스레드 큐에 적재
- 따라서 Dispose 드레인 중에 새 이벤트가 추가되는 코드 경로가 없음
- `ImmediateFinalize()` 경로(워커 미등록)에서는 `EnqueueEvent`를 호출하는 외부 코드가 없음
- `EnqueueEvent` 반환 false 시 `TrySetCanceled()` 처리 (`LoginProcessor.cs:85-88`)

**실제 버그 경로 없음.**

### SessionSystem Disconnect / PlayerGameEnter 순서 race

```
Case 1 (Disconnect → PlayerGameEnter 순서로 큐 도착):
  InternalDisconnectSession: _sessions.TryRemove → ImmediateFinalize()
  InternalPlayerGameEnter: _sessions.TryGetValue 실패 → tcs.TrySetCanceled()
  → LoginProcessor catch(OperationCanceledException) → 안전 종료

Case 2 (PlayerGameEnter → Disconnect 순서로 큐 도착):
  InternalPlayerGameEnter: IsDisconnected == false → Add → SetEntryHandshakeCompleted
  InternalDisconnectSession: IsEntryHandshakeCompleted == true → DisconnectForNextTick()
  → 워커에서 DisconnectAsync 실행 → 정상 정리
```

두 경우 모두 정확히 처리됨. ConcurrentQueue FIFO 보장 + SessionSystem 단일 스레드가 핵심.

### 셧다운 순서

```
1. boundChannel.CloseAsync() — 신규 연결 수락 중단
2. SessionSystem.Stop() — 모든 세션 Disconnect enqueue + 스레드 종료
3. PlayerSystem.WaitUntilEmptyAsync(30s) — 모든 플레이어 DB write 완료 대기
4. PlayerSystem.Stop() — 워커 스레드 종료
5. EventLoopGroup.DisposeAsync() — I/O 이벤트 루프 종료 (await using 스코프 끝)
```

기존 세션 정리 완료 후 EventLoopGroup 종료. 순서 정확.

---

## 수정 우선순위 요약

| 우선순위 | ID | 내용 | 파일 |
|----------|-----|------|------|
| 즉시 | BUG-1 | 인증 전 패킷 큐 무제한 적재 | `GameServerHandler.cs:48-51`, `SessionComponent.cs:19` |
| 높음 | BUG-2 | 동일 계정 다중 로그인 미방지 | `LoginProcessor.cs:22`, `PlayerSystem.cs` |
| 낮음 | IMP-1 | `_running` volatile 누락 | `BaseWorker.cs:12`, `SessionSystem.cs:43` |
