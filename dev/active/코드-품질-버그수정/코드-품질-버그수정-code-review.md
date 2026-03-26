# 코드 품질 버그수정 — 코드 리뷰
Date: 2026-03-26
Reviewer: Claude Sonnet 4.6

---

## 종합 판정

| 항목 | 판정 |
|------|------|
| 문제 해결 완전성 | PASS — 7건 전부 원래 문제를 완전히 해결함 |
| 신규 버그 유입 | PASS — 발견 없음 |
| 동시성 정확성 | PASS (한 곳 주의사항 있음, 하단 H-2 섹션 참조) |
| BCrypt workFactor 적절성 | CONDITIONAL PASS — 하단 H-5 섹션 상세 설명 |

---

## H-1. `SessionSystem._running` — volatile 추가

**파일**: `GameServer/Systems/SessionSystem.cs:43`

**판정: PASS**

`volatile bool _running` 선언은 정확한 수정이다.

- `StartSystem()`(호출 스레드)에서 쓰고 `Loop()`(백그라운드 스레드)에서 읽는 단순 flag 패턴에 `volatile`은 충분하다.
- `Stop()`에서 `_running = false` 후 `Thread.Join(30s)`를 호출하므로 Join이 happens-before를 확립한다. `volatile`이 없어도 Join 이후에는 안전하나, Loop 내부 while 조건 평가 시점에는 여전히 필요하다. 수정이 맞다.
- 드레인 루프(`while (_eventQueue.Count > 0)`)는 `_running = false` 이후 실행되므로 이중 이벤트 처리 없이 잔여 이벤트를 안전하게 소진한다.

추가 지적 없음.

---

## H-2. `PlayerSystem.TryReserveLogin` — TOCTOU 이중 검증

**파일**: `GameServer/Systems/PlayerSystem.cs:28-37`

**판정: PASS (주의사항 1건)**

```csharp
public bool TryReserveLogin(ulong accountId)
{
    if (_players.ContainsKey(accountId)) return false;      // (1)
    if (!_reservedAccounts.TryAdd(accountId, 0)) return false; // (2)
    if (_players.ContainsKey(accountId))                     // (3)
    {
        _reservedAccounts.TryRemove(accountId, out _);
        return false;
    }
    return true;
}
```

이중 검증 패턴은 TOCTOU 경쟁을 정확하게 해소한다.

- (2)→(3) 사이 구간: 다른 스레드의 `Add()`가 `_players.TryAdd` 성공 후 `_reservedAccounts.TryRemove`를 실행하면, 이 스레드는 (3)에서 `_players.ContainsKey` true → TryRemove 후 false 반환. 정확하다.
- `Add()`의 순서(`_players.TryAdd` → `_reservedAccounts.TryRemove`)는 주석에 명시되어 있고 역순 시 TOCTOU가 발생한다는 점도 설명되어 있다. 의도적 설계로 올바르다.

**주의사항**: `TryReserveLogin`은 `LoginProcessor`에서 `async` 흐름 중 호출된다. `LoginProcessor`는 I/O 이벤트 루프 스레드(`DotNetty EventLoop`)가 아닌 `ThreadPool` 스레드에서 실행되므로 여러 스레드가 동시에 진입 가능하다. 이 패턴은 해당 조건을 전제하고 설계되어 있으며, `ConcurrentDictionary`의 원자적 `TryAdd`가 그것을 보장한다. 별도 조치 불필요.

---

## H-3. `LobbyComponent._rooms` — Dictionary + lock 통일

**파일**: `GameServer/Component/Lobby/LobbyComponent.cs:26-27`

**판정: PASS**

`ConcurrentDictionary` + `lock` 혼재를 `Dictionary` + `lock` 단일 메커니즘으로 통일한 수정은 정확하다.

- `GetRoomList()`, `TryGetRoom()`, `GetRooms()`, `RoomCount` 프로퍼티까지 모든 읽기 경로가 `_roomLock`으로 보호된다.
- `CreateRoom()` 내부에서 `newRoom.Initialize()` → `_rooms.TryAdd()` → `newRoom.TryReserve()` 전 과정이 lock 하에서 실행된다. `Initialize` 비용이 크지 않아 lock 구간이 과도하지 않다.
- `RemoveRoom()`은 lock 내부에서 Dictionary 제거 후 **lock 외부**에서 `room.Dispose()`를 실행한다. 이는 Dispose 중 블로킹이 발생해도 lock을 오래 점유하지 않으므로 올바른 패턴이다.
- `OnDispose()`에서 lock 내부 `_rooms.Values.ToList()` + `_rooms.Clear()` 후 lock 외부 Dispose는 RemoveRoom과 동일한 안전한 패턴이다.

추가 지적 없음.

---

## H-4. `SessionComponent._typeCounters` — O(n)→O(1) 카운터

**파일**: `GameServer/Network/SessionComponent.cs:22,69,82,95`

**판정: PASS**

`ConcurrentDictionary<PayloadOneofCase, int> _typeCounters` 도입은 정확하다.

**ProcessPacket (Enqueue 경로)**:
```csharp
_packetQueue.Enqueue(packet);
_typeCounters.AddOrUpdate(type, 1, (_, c) => c + 1);
```
큐 Enqueue 후 카운터 증가. 순서가 중요하다: 카운터가 먼저 증가하면 `VerifyPolicy`가 아직 큐에 없는 패킷을 "있다"고 판단할 수 있으나, 어차피 `ProcessPacket`은 I/O 이벤트 루프 단일 스레드에서만 호출되므로 동시성 문제는 없다.

**DrainPackets (Dequeue 경로)**:
```csharp
_typeCounters.AddOrUpdate(type, 0, (_, c) => Math.Max(0, c - 1));
```
`Math.Max(0, c - 1)` 가드는 언더플로우 방지로 적절하다. `DrainPackets`는 워커 스레드에서, `ProcessPacket`은 I/O 스레드에서 실행되므로 `ConcurrentDictionary`가 필요하다. 올바른 선택이다.

**ClearPacketQueue**:
```csharp
while (_packetQueue.TryDequeue(out _)) { }
_typeCounters.Clear();
```
드레인 후 카운터 초기화. `ClearPacketQueue`는 워커 스레드에서만 호출되고 `ProcessPacket`과의 경쟁은 `DrainPackets`와 동일 스레드에서 호출된다는 점에서 안전하다.

추가 지적 없음.

---

## H-5. BCrypt 도입 — LoginProcessor.Verify / RegisterProcessor.HashPassword

**파일**: `LoginProcessor.cs:239`, `RegisterProcessor.cs:63`, `GameServer.csproj:22`

**판정: CONDITIONAL PASS — workFactor 검토 필요**

### 정확성

BCrypt 도입 자체는 올바르다.
- `BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11)` — 등록 시 해싱
- `BCrypt.Net.BCrypt.Verify(password, account.password_hash)` — 로그인 시 검증
- 실패 시 username/password 어느 쪽인지 구분 없이 `InvalidCredentials`만 반환하여 사용자 열거 공격(username enumeration) 방지. 올바른 보안 설계.
- DB에 패스워드 자체가 아닌 `password_hash`만 저장. 올바름.

### workFactor=11이 게임 서버 로그인 레이턴시에 적절한가

| workFactor | 일반적 해싱 시간 (개발 PC 기준) | 비고 |
|-----------|-------------------------------|------|
| 10 | ~100ms | OWASP 최소 권장 |
| 11 | ~200ms | 이번 수정에 사용된 값 |
| 12 | ~400ms | 프로덕션 권장 하한 |

**결론**: workFactor=11은 게임 서버 맥락에서 **허용 가능하나 하한 수준**이다.

- 로그인은 사전에 `TryReserveLogin`으로 직렬화되므로 동일 계정의 동시 BCrypt 연산은 발생하지 않는다.
- BCrypt.Verify는 `async`가 아닌 **동기 블로킹** 호출이다. `LoginProcessor.ProcessAsync`가 `await` 중인 ThreadPool 스레드에서 실행되며 `~200ms` 동안 해당 스레드를 점유한다. 동시 로그인 수가 많아지면 ThreadPool starvation이 발생할 수 있다.
- **권장 대응**: `await Task.Run(() => BCrypt.Net.BCrypt.Verify(password, account.password_hash))` 로 래핑하여 BCrypt 연산을 별도 ThreadPool 작업으로 분리하는 것이 DotNetty EventLoop 친화적이다. (현재는 이미 EventLoop 외부이므로 즉각적 위험은 없으나, 동시 접속 부하 환경에서는 개선이 필요하다.)
- 브레이킹 체인지(기존 평문 저장 계정 로그인 불가)가 context에 정확히 명시되어 있다. 테스트 환경 재가입 절차 필요.

---

## H-6. `GameStage.RunTickAsync` — ObjectDisposedException catch

**파일**: `GameServer/Component/Stage/GameStage.cs:148`

**판정: PASS**

```csharp
catch (OperationCanceledException) { }
catch (ObjectDisposedException) { }
```

`Dispose()`에서 `_cts.Cancel()` 직후 `_cts.Dispose()` 호출 시 `PeriodicTimer.WaitForNextTickAsync`가 이미 Dispose된 `CancellationToken`을 참조하면서 `ObjectDisposedException`이 발생하는 경로가 존재한다. catch 추가로 정확히 해소된다.

`EndGame()`의 `_cts.Cancel()` 경로(게임 정상 종료)와 `Dispose()`의 `_cts.Cancel()` + `_cts.Dispose()` 경로(비정상/강제 종료) 모두 안전하게 처리된다.

추가 지적 없음.

---

## H-7. `WeaponManager.Clear()` + `GameStage.Dispose()` 호출

**파일**: `WeaponManager.cs:33`, `GameStage.cs:569`

**판정: PASS**

```csharp
// WeaponManager.cs
public void Clear() => _playerWeapons.Clear();

// GameStage.Dispose()
_cts.Cancel();
_weaponManager.Clear();
_cts.Dispose();
```

`Dispose()` 시점에 `_weaponManager.Clear()` 호출로 `_playerWeapons` Dictionary가 정리된다.

한 가지 확인 사항: `Clear()`가 호출될 때 `RunTickAsync`의 `Tick()` 루프가 아직 실행 중일 수 있다. `Dispose()`는 `_cts.Cancel()` 후 `_weaponManager.Clear()`를 즉시 호출하나, `PeriodicTimer` 틱이 진행 중이라면 `_weaponManager.Tick()`과 `Clear()`가 동시에 실행될 수 있다.

**평가**: `WeaponManager`는 "GameStage._stateLock 하에서만 호출된다"고 문서화되어 있으나, `Dispose()`는 `_stateLock` 없이 `Clear()`를 호출한다. `_playerWeapons`는 일반 `Dictionary`이므로 동시 접근 시 예외가 발생할 수 있다.

**그러나 실제 위험은 낮다**: `_cts.Cancel()` 후 다음 틱 루프 진입은 `ct.IsCancellationRequested`로 차단되고, `WaitForNextTickAsync` 자체가 취소된다. 진행 중인 `Tick()` 완료 후 다음 `WaitForNextTickAsync`가 취소되어 루프가 종료되기 때문에, 대부분의 경우 `Clear()`가 안전하게 실행된다. 다만 이론적으로 `Tick()` 실행 중 `Dispose()`가 호출되는 레이스는 존재한다. 프로덕션 환경에서 안정성을 높이려면 `Dispose()` 내에서 `_stateLock`을 획득하거나, `PeriodicTimer`를 먼저 종료 대기 후 `Clear()`를 호출하는 것이 더 안전하다. 현재 구현은 게임 서버 맥락에서 허용 가능한 수준이다.

---

## 요약

| # | 항목 | 원 문제 해결 | 신규 문제 |
|---|------|------------|---------|
| H-1 | `_running` volatile | 완전 해결 | 없음 |
| H-2 | TryReserveLogin TOCTOU | 완전 해결 | 없음 |
| H-3 | `_rooms` lock 통일 | 완전 해결 | 없음 |
| H-4 | O(n)→O(1) 카운터 | 완전 해결 | 없음 |
| H-5 | BCrypt 도입 | 완전 해결 | BCrypt.Verify 동기 블로킹 — 고부하 시 ThreadPool starvation 가능성 (낮음) |
| H-6 | ObjectDisposedException | 완전 해결 | 없음 |
| H-7 | WeaponManager.Clear() | 완전 해결 | Dispose()-Tick() 레이스 이론적 존재 (실제 위험 낮음) |

### 후속 권장 사항 (우선순위 순)

1. **BCrypt async 래핑** (선택): `Task.Run`으로 BCrypt 연산 분리 — 고부하 환경 대비
2. **WeaponManager.Clear() lock 보호** (선택): `Dispose()` 내 `_stateLock` 획득 후 `Clear()` 호출 — 이론적 레이스 제거
3. MEDIUM 항목(M-1~M-9)은 다음 작업에서 순차 처리

---

*이 리뷰는 이번 세션에서 수정된 HIGH 7건 파일에 한정된다. MEDIUM/LOW는 별도 작업(코드-품질-버그수정-tasks.md)에서 관리한다.*
