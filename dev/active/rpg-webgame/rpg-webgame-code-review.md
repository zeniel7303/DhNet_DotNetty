# RpgRoomScenario / PveStressScenario 코드 아키텍처 리뷰

- 리뷰 대상: `TestClient/Scenarios/RpgRoomScenario.cs`, `TestClient/Scenarios/PveStressScenario.cs`, `TestClient/Program.cs`
- 참고 기준: `BaseRoomScenario.cs`, `RoomLoopScenario.cs`, `ClientContext.cs`
- 리뷰 날짜: 2026-03-24

---

## 종합 평가

두 시나리오 모두 `BaseRoomScenario` 템플릿 메서드 패턴을 정확히 준수하며, 기존 코드베이스의 스타일(fire-and-forget + try/catch, volatile bool 가드, 단방향 협조 쌍 구조)과 일관성이 높다. 크리티컬 버그는 없으나, 아래에 정리한 5개 영역에서 잠재적 위험 및 개선 여지가 존재한다.

---

## 1. 비동기 패턴 — Task.Run fire-and-forget, 예외 처리

### 현황

`OnLoginSuccessAsync`, `HandleRoomListAsync`, `NotiGameEnd`, `ResRoomExit`, `StartCycleAsync` 등 딜레이가 필요한 모든 분기에서 `_ = Task.Run(async () => { try { ... } catch { log } })` 패턴을 사용한다.

### 평가

**양호.** 기존 `RoomLoopScenario`와 동일한 관용구이며, 예외를 삼키지 않고 `GameLogger.Error`로 기록한다. DotNetty I/O 스레드를 블로킹하지 않는다는 점도 올바르다.

### 지적 사항

**[경미]** `ResEnterGame` 처리에서 `RunGameActionsAsync` 호출 방식이 두 시나리오 모두 아래와 같다.

```csharp
// RpgRoomScenario.cs:108, PveStressScenario.cs:81
_ = Task.Run(async () => await RunGameActionsAsync(channel, ctx));
```

`Task.Run`으로 감싸는 것은 불필요한 ThreadPool 전환이다. `RunGameActionsAsync` 자체가 비동기 메서드이므로 다음과 같이 써도 동일하게 I/O 스레드를 블로킹하지 않는다.

```csharp
_ = RunGameActionsAsync(channel, ctx);
```

`Task.Run`을 추가하면 ThreadPool 스레드를 하나 더 소비하며, 대규모 부하 테스트(100+ 클라이언트)에서는 불필요한 컨텍스트 스위치가 누적된다.

**[경미]** `RoomLoopScenario`의 `NotiRoomChat` 핸들러에는 `channel.Active` 체크가 없으나, 두 신규 시나리오의 `HandleRoomListAsync` 재시도 블록에는 체크가 있다. 일관성은 있으나 `RoomLoopScenario` 측이 개선 대상임을 참고로 기록한다.

---

## 2. 경쟁 조건 — `volatile bool _gameEnded` 충분한지, Interlocked 필요 여부

### 현황

```csharp
// 두 파일 공통
private volatile bool _gameEnded;
```

쓰기: `NotiGameEnd` 핸들러 (DotNetty I/O 스레드).
읽기: `RunGameActionsAsync` 루프 조건 (`Task.Run` → ThreadPool 스레드).

### 평가

**현재 구조에서는 `volatile`으로 충분하다.**

- `_gameEnded`는 `false → true` 단방향 전이만 발생한다. 한 번 `true`가 되면 다시 `false`로 되돌리지 않는다 (RpgRoomScenario 기준).
- `volatile`은 쓰기 후 즉시 모든 스레드에 가시성을 보장하므로, 단방향 플래그에는 적합하다.
- `Interlocked.CompareExchange`가 필요한 시나리오는 "두 스레드가 동시에 쓰기를 경쟁할 때" 또는 "읽기-수정-쓰기가 원자적이어야 할 때"이므로, 여기서는 해당하지 않는다.

### PveStressScenario 추가 주의

`PveStressScenario`는 `ResEnterGame` 핸들러에서 `_gameEnded = false` 재설정이 발생한다.

```csharp
// PveStressScenario.cs:74
_gameEnded = false;
```

이 시점에 이전 사이클의 `RunGameActionsAsync`가 아직 실행 중일 경우(`NotiGameEnd`보다 지연된 경로), `_gameEnded`가 `false`로 덮어써져 이전 루프가 추가 패킷을 전송할 수 있다. 실제로 이 가능성이 발생하려면 "게임 종료 → 룸 퇴장 → 다음 사이클 시작 (3초 딜레이)" 동안 이전 `RunGameActionsAsync`가 8.3초 이상 잔류해야 하므로 실용적으로는 거의 발생하지 않는다. 그러나 이론상 위험은 존재한다.

**개선 제안 (선택적):** `_cycleId`(int)를 도입하고 `RunGameActionsAsync` 시작 시 현재 사이클 ID를 캡처하여, 루프 중 ID가 변경되면 조기 종료하는 방식으로 완전히 격리할 수 있다.

---

## 3. `channel.Active` 체크 후 `WriteAndFlushAsync` race condition

### 현황

```csharp
// 공통 패턴
if (!channel.Active) return;
await channel.WriteAndFlushAsync(...);
```

### 평가

**알려진 TOCTOU(Time-of-Check-Time-of-Use) 패턴이며, 이 코드베이스의 관용구로 허용 가능하다.**

`channel.Active` 체크와 `WriteAndFlushAsync` 사이에 채널이 닫힐 수 있다. 그러나:

1. DotNetty의 `WriteAndFlushAsync`는 채널이 비활성화된 상태에서 호출되면 `ClosedChannelException`을 반환하는 `Task`를 돌려준다 (throw하지 않고 faulted Task).
2. 해당 예외는 fire-and-forget 패턴의 `catch (Exception ex)` 블록에서 잡힌다.
3. 따라서 프로세스가 크래시되거나 상태가 오염되는 일은 없다.

**체크의 목적은 불필요한 패킷 전송 시도를 줄이는 early-return이지, 완벽한 동기화 장치가 아니다.** 이 용도로는 충분하다.

### 지적 사항 (경미)

`RpgRoomScenario`의 `RunGameActionsAsync` 내부에서는 루프 조건(`channel.Active && !_gameEnded`)과 루프 본문 직전 중복 체크(`if (!channel.Active || _gameEnded) break`)가 모두 존재한다. 이중 체크는 안전하지만 중복이다. 코드 간결성을 위해 루프 조건 하나로 통일할 수 있다.

---

## 4. 시나리오 흐름 논리 (짝수/홀수 클라이언트 협조, 룸 입장/Ready 타이밍)

### 흐름 요약

```
짝수 [0,2,4,...]:  로그인 → ReqCreateRoom
홀수 [1,3,5,...]:  로그인 → (2초 대기) → ReqRoomList → ReqRoomEnter → ReqReadyGame

짝수: NotiRoomEnter(타인) 수신 → ReqReadyGame
→ 양쪽 Ready → NotiGameStart → ResEnterGame → RunGameActionsAsync
```

### 평가

**흐름 설계 자체는 올바르다.** 다만 아래 두 가지 엣지 케이스를 주목한다.

**[주의] 홀수 클라이언트가 입장 시 방이 이미 시작된 경우**

`HandleRoomListAsync`는 `!r.IsStarted && r.PlayerCount < r.MaxPlayers` 조건으로 필터링한다. 입장 가능한 방이 없으면 2초 후 재시도한다. 그러나 짝수 클라이언트가 룸을 생성하지 못했거나(서버 오류), 룸 목록 응답이 오기 전에 방이 시작되어 버렸을 때 홀수 클라이언트는 재시도 루프를 반복하다 채널이 닫힐 때까지 대기한다. 부하 테스트 환경에서는 허용 가능하지만, 최대 재시도 횟수 제한이 없으므로 짝수 클라이언트 연결이 끊겼을 때 홀수 클라이언트는 영구 대기 상태가 된다.

**[주의] 짝수 클라이언트의 Ready 조건: `noti.PlayerId != ctx.PlayerId`**

`NotiRoomEnter`는 입장한 플레이어 자신에게도 브로드캐스트될 수 있다. 코드는 `PlayerId`로 자신을 필터링하는데, `ctx.PlayerId`가 `ResLogin` 이후 정확히 설정되어 있어야 한다. `BaseRoomScenario.OnPacketReceivedAsync`에서 `ctx.PlayerId = packet.ResLogin.PlayerId`가 명시적으로 설정되므로 이 전제는 보장된다. **안전하다.**

**[경미] PveStressScenario에서 NotiGameStart 로그 누락**

`RpgRoomScenario`는 `NotiGameStart`에 로그를 남기지만, `PveStressScenario`는 `NotiReadyGame`과 `NotiGameStart`를 단일 `case` 그룹으로 묶고 로그 없이 `return true`한다. 스트레스 시나리오에서는 로그 최소화가 의도적일 수 있으므로 버그는 아니다.

---

## 5. PveStressScenario 루프 완결성 — NotiGameEnd → ResRoomExit → 다음 사이클 흐름

### 흐름 검증

```
ResEnterGame → RunGameActionsAsync (fire-and-forget)
     |
NotiGameEnd 수신
     ├─ _gameEnded = true
     ├─ _cycleCount++
     └─ Task.Run: 1초 대기 → ReqRoomExit 전송

ResRoomExit 수신
     ├─ ctx.RoomExitScheduled = false
     └─ Task.Run: 2초 대기 → StartCycleAsync

StartCycleAsync
     ├─ 짝수: ReqCreateRoom
     └─ 홀수: 3초 대기 → ReqRoomList
```

### 평가

**루프 완결성은 확보되어 있다.** `NotiGameEnd → ResRoomExit → StartCycleAsync`의 선형 체인이 명확하다.

### 지적 사항

**[버그 가능성, 경미] `ctx.RoomExitScheduled` 사용 불일치**

`ResRoomExit` 핸들러에서 `ctx.RoomExitScheduled = false`를 초기화하지만, `NotiGameEnd`에서 `ReqRoomExit`를 전송하기 전에 `ctx.RoomExitScheduled = true`로 설정하는 코드가 없다. `RoomLoopScenario`에서는 이 플래그를 이중 전송 방지에 활용하지만, `PveStressScenario`에서는 설정만 하고 체크하지 않으므로 사실상 데드 코드가 된다.

`NotiGameEnd`가 클라이언트에 두 번 도달하는 경우(예: 네트워크 재전송, 버그)에 `ReqRoomExit`도 두 번 전송될 수 있다. 방어 코드로 `ctx.RoomExitScheduled` 플래그를 `NotiGameEnd`에서 설정하고 체크하면 이중 전송을 막을 수 있다.

**[경미] 채팅 메시지의 사이클 번호 오차**

```csharp
// PveStressScenario.cs:261
Message = $"[Bot{ctx.ClientIndex}] Cycle#{_cycleCount + 1} GG!"
```

`_cycleCount`는 `NotiGameEnd` 수신 시 증가한다. `RunGameActionsAsync`가 게임 도중 실행되는 시점에는 아직 `NotiGameEnd`가 도달하지 않았으므로 `_cycleCount`는 현재 사이클 번호보다 1 작다. `+1` 보정은 이를 의식한 것으로 보이나, `NotiGameEnd`로 인해 `_gameEnded = true`가 되어 채팅이 실제로 전송되는 경우는 드물다(공격 루프가 채팅 전에 종료될 가능성). 논리적 오류는 아니지만 가독성을 위해 `_cycleCount`를 `ResEnterGame` 진입 시점에 증가시키는 설계가 더 직관적이다.

**[정보] 홀수 클라이언트의 StartCycleAsync 딜레이 차이**

- `RpgRoomScenario`: 홀수 클라이언트 첫 딜레이 2초
- `PveStressScenario.StartCycleAsync`: 홀수 클라이언트 딜레이 3초

스트레스 시나리오에서 3초를 준 것은 다수의 짝수 클라이언트가 룸을 동시에 생성하는 데 여유 시간을 주기 위한 의도로 보인다. 의도적인 설계로 판단한다.

---

## 요약 테이블

| 항목 | 심각도 | 파일 | 내용 |
|------|--------|------|------|
| `Task.Run` 불필요 래핑 | 경미 | 두 파일 공통 | `RunGameActionsAsync`를 `Task.Run`으로 감쌀 필요 없음 |
| `_gameEnded` 재설정 경쟁 | 경미 | PveStressScenario | 이전 사이클 루프 잔류 시 이론상 오동작 가능 |
| `channel.Active` TOCTOU | 정보 | 두 파일 공통 | 설계상 허용된 관용구, faulted Task로 안전하게 처리됨 |
| 루프 내 이중 체크 중복 | 경미 | RpgRoomScenario | 루프 조건과 내부 `if break` 중복 |
| 홀수 클라이언트 영구 대기 | 경미 | 두 파일 공통 | 짝수 클라이언트 소멸 시 재시도 상한 없음 |
| `RoomExitScheduled` 미설정 | 경미 | PveStressScenario | NotiGameEnd에서 플래그 미설정으로 이중 전송 방어 미작동 |
| 채팅 사이클 번호 보정 | 정보 | PveStressScenario | `_cycleCount + 1` 오프셋 — 버그 아니나 가독성 저하 |
| `NotiGameStart` 로그 누락 | 정보 | PveStressScenario | 의도적 생략으로 추정, 버그 아님 |

---

## 결론

두 시나리오는 기존 코드베이스의 패턴과 잘 통합되어 있으며, 부하 테스트 도구로서의 기본 기능은 완전히 충족한다. 위에 열거된 항목 중 **즉시 수정을 권장하는 크리티컬 버그는 없다.** 다만 `PveStressScenario`의 `RoomExitScheduled` 미설정과 `Task.Run` 불필요 래핑은 가볍게 개선할 가치가 있다. `_gameEnded` 재설정 경쟁 조건은 스트레스 테스트 규모가 커질수록 재현 가능성이 낮아지므로 단기적으로는 허용 가능하다.
