# 보안 3단계 Phase 2 — 코드 리뷰 (최종)
Last Updated: 2026-03-20

## 요약

Phase 2 (회원가입/비밀번호 인증) 구현은 전체적으로 안정적이다. 핵심 흐름(Register → Login → PlayerGameEnter → LobbyEnter), 중복 방지 플래그(`TrySetRegisterStarted`/`TrySetLoginStarted`), DB 실패 경로의 응답 보장, Disconnect 정합성, 에러 응답 후 return이 모두 올바르게 구현되어 있다. Phase 3(BCrypt) 전환 포인트도 주석으로 명시되어 있다.

Critical 항목 없음. Warning 2건, 경미한 개선 제안 6건.

---

## 🔴 Critical

해당 없음.

---

## 🟡 Warning

### W-1: 평문 비밀번호 네트워크 전송 및 DB 저장 (Phase 2 의도적 한계)

**위치**: `register.proto`, `login.proto`, `RegisterProcessor.cs:81`, `AccountRow.cs:9`

`ReqRegister.password`와 `ReqLogin.password`가 평문으로 소켓을 통해 전달되며, DB `accounts.password_hash` 컬럼에도 평문이 저장된다. Phase 2에서 의도된 설계이고 코드에 교체 포인트 주석이 명시되어 있어 인지된 한계이다.

다만 네트워크 스니핑 시 모든 비밀번호가 노출되므로, Phase 3 전환 전까지는 로컬 테스트 환경 외에 서버를 외부 네트워크에 노출해서는 안 된다.

**Phase 3 전환**: `BCrypt.HashPassword(password, workFactor: 11)` 저장 + `BCrypt.Verify()` 검증으로 교체. BCrypt는 CPU 집약적이므로 `await Task.Run(() => BCrypt.HashPassword(...))` 패턴으로 ThreadPool 실행 필요.

---

### W-2: Timing Attack — Phase 3 전환 시 Username Enumeration 노출 위험

**위치**: `LoginProcessor.cs:166`

```csharp
if (account == null || account.password_hash != password)
```

현재 Phase 2 코드에서는 `account == null` 여부와 무관하게 DB 조회가 항상 먼저 완료된 후 분기하므로 타이밍 차이가 거의 없다. 그러나 Phase 3에서 `BCrypt.Verify()`로 교체할 때 단순 대입 방식을 유지하면 문제가 생긴다. `account == null`인 경우 BCrypt.Verify를 건너뛰므로 존재하지 않는 username은 존재하는 username보다 수십~수백 ms 빠르게 응답하여 유효한 username 열거가 가능해진다.

**Phase 3 안전한 패턴**:

```csharp
// account가 null이어도 항상 BCrypt.Verify를 실행해 타이밍 균일화
private static readonly string _dummyHash =
    BCrypt.HashPassword("dummy", workFactor: 11);

var hashToVerify = account?.password_hash ?? _dummyHash;
var valid = account != null
    && await Task.Run(() => BCrypt.Verify(password, hashToVerify));

if (!valid) { /* INVALID_CREDENTIALS */ }
```

Phase 2에서는 지금 구현이 올바르다. Phase 3 전환 시 이 패턴을 빠뜨리지 않도록 코드 주석에 경고를 추가해 두는 것이 좋다.

---

## 🟢 Good

### G-1: username/password 검증 순서 및 return 처리

`RegisterProcessor.cs`의 검증 순서가 올바르다. `username.Trim()` 후 길이 검증(line 23) → password 길이 검증(line 34) → DB 중복 확인(line 52) 순으로 빠른 실패 원칙을 지킨다. 모든 에러 분기(INVALID_USERNAME_LENGTH, INVALID_PASSWORD_LENGTH, USERNAME_TAKEN, DB_ERROR)에서 return이 빠짐없이 존재한다. `LoginProcessor.cs`의 에러 분기도 동일하게 return이 모두 있다.

---

### G-2: username Trim 처리 — 공백 전용 입력 자동 거부

`RegisterProcessor.cs:19`의 `req.Username.Trim()` 처리로 공백만 있는 username("   ")이 Trim 후 길이 0이 되어 INVALID_USERNAME_LENGTH로 거부된다. 빈 문자열도 동일하게 처리된다. password는 Trim하지 않는데, 이는 비밀번호 앞뒤 공백이 유효한 문자이므로 올바른 설계이다.

---

### G-3: INSERT IGNORE + rows_affected == 0 검사 — TOCTOU race condition 방어

`RegisterProcessor.cs`의 SELECT → INSERT 사이에 다른 세션이 동일 username으로 삽입할 수 있는 TOCTOU 취약점을 DB 레벨에서 방어한다.

- `accounts` 테이블의 `UNIQUE KEY ux_accounts_username`이 중복 삽입을 차단한다.
- `INSERT IGNORE`가 rows_affected=0을 반환하고, line 96의 `if (inserted == 0)` 체크가 이를 USERNAME_TAKEN으로 처리한다.
- 같은 세션에서의 중복 실행은 `TrySetRegisterStarted()`가 Interlocked으로 차단한다.

---

### G-4: INSERT 후 SELECT로 account_id 재조회 — 실제 race condition이 아님

`RegisterProcessor.cs:109`에서 INSERT 성공 후 `SelectByUsernameAsync`로 account_id를 재조회하는 패턴에 대해 race condition 우려가 있을 수 있으나 실제 문제는 아니다. DELETE 경로가 현재 시스템에 존재하지 않으므로, INSERT 성공 이후 해당 username 레코드가 사라질 수 없다. `created!.account_id`의 null-forgiving 연산자는 안전하다. 다만 SELECT 왕복 비용은 구조적 비효율이므로 Phase 3에서 `LAST_INSERT_ID()` 방식으로 개선할 수 있다.

---

### G-5: CloseAsync() → ChannelInactive → SessionSystem.EnqueueDisconnect 경로 완전성

`LoginProcessor.cs:17`의 `session.Channel.CloseAsync()` 호출이 올바르게 Disconnect 처리로 이어진다.

경로: `CloseAsync()` → DotNetty `ChannelInactive` 콜백 → `GameServerHandler.ChannelInactive()`(line 20-26) → `SessionSystem.Instance.EnqueueDisconnect(_session)` → `InternalDisconnectSession()`.

`InternalDisconnectSession`은 `IsDisconnected` 멱등성 보장, `IsEntryHandshakeCompleted` 분기(DisconnectForNextTick vs ImmediateFinalize) 처리를 완전하게 수행한다. 세션 누수 없음.

---

### G-6: INVALID_CREDENTIALS — username 존재 여부 미노출 (User Enumeration 방어)

`LoginProcessor.cs:165-173`에서 `account == null`과 `account.password_hash != password` 두 경우를 동일한 `InvalidCredentials` 에러 코드로 처리한다. 공격자가 username 존재 여부를 열거하는 User Enumeration 공격을 로직 레벨에서 방어하는 올바른 패턴이다.

---

### G-7: 에러코드 번호 일관성 및 대역 설계

`error_codes.proto`의 대역 설계가 명확하고 일관되다.

- 시스템(1000~): SERVER_FULL=1000, DB_ERROR=1001, INVALID_USERNAME_LENGTH=1002, INVALID_PASSWORD_LENGTH=1003, USERNAME_TAKEN=1004, INVALID_CREDENTIALS=1005
- 로비(2000~), 룸(3000~) 으로 성격별 대역이 분리되어 있다.

ResRegister에서 username 길이 위반 시 `ErrorCode.InvalidUsernameLength`(1002)를 올바르게 반환한다(RegisterProcessor.cs:28). 번호가 1002이므로 proto 정의와 일치한다.

---

### G-8: 클라이언트 시나리오 Register → Login 흐름 일관성

세 시나리오(BaseRoomScenario, LobbyChatScenario, ReconnectStressScenario) 모두 아래 패턴을 일관되게 준수한다.

1. `OnConnectedAsync`: `ReqRegister` 전송 (`ctx.Password = "0000"` 사용)
2. `ResRegister` 수신: `SUCCESS` 또는 `USERNAME_TAKEN` → `ReqLogin` 전송 / 그 외 → 경고 로그
3. `ResLogin` 수신: `SUCCESS` → 시나리오별 다음 단계 진행

`USERNAME_TAKEN`을 로그인 진행 조건에 포함한 것이 재접속 시나리오에서 특히 중요하며, 세 시나리오 모두 이를 올바르게 처리한다.

---

### G-9: ReconnectStressScenario — ResRegister 비정상 에러 시 처리

`ReconnectStressScenario.cs:74-77`에서 SUCCESS/USERNAME_TAKEN 외의 에러(INVALID_USERNAME_LENGTH, INVALID_PASSWORD_LENGTH, DB_ERROR 등)가 오면 `LoadTestStats.IncrementErrors()` 후 `channel.CloseAsync()`를 호출한다. 이 경로에서 `ChannelInactive`가 발화하고 `OnDisconnected`가 호출되어 `_disconnectTcs.TrySetResult()`를 수행함으로써 재접속 루프가 다음 사이클을 진행한다. 에러 시나리오에서도 루프가 막히지 않는다.

---

### G-10: ResetForReconnect에서 Password 유지

`ClientContext.ResetForReconnect()`(line 23-30)는 PlayerId, PlayerName, RoomEnterSent, RoomExitScheduled, RoomEnterRetryCount만 초기화하며 `Password`를 초기화하지 않는다. 재접속 후에도 `ctx.Password = "0000"`이 유지되어 ReqRegister/ReqLogin 모두 동일한 비밀번호로 전송된다. 의도된 동작이며 올바르다.

---

### G-11: accounts 및 players 테이블 스키마

`schema_game.sql`의 테이블 정의가 정확하다.

- `accounts.username` — `VARCHAR(64) NOT NULL` + `UNIQUE KEY ux_accounts_username` — INSERT IGNORE의 동작 기반.
- `accounts.password_hash` — `VARCHAR(255)` — BCrypt 해시(`$2a$11$...`, 최대 60자)를 충분히 수용.
- `players.account_id` — `BIGINT UNSIGNED NULL DEFAULT NULL` — nullable FK로 올바르게 추가됨. `PlayerRow.account_id`도 `ulong?`로 일치함.
- `accounts` 테이블에 `accounts.account_id`에 FK 제약 선언은 없으나, 현재 구조에서 ON DELETE CASCADE 등의 요건이 없으므로 Phase 2 범위에서는 적절하다.

---

## Phase 3 전환 체크리스트

- [ ] `BCrypt.Net-Next` NuGet 패키지 추가 (GameServer, GameServer.Database 프로젝트)
- [ ] `RegisterProcessor.cs:81` — `password_hash = await Task.Run(() => BCrypt.HashPassword(password, 11))`로 교체
- [ ] `LoginProcessor.AuthenticateAsync` — dummy hash 패턴 적용 (W-2 대응, 타이밍 균일화)
- [ ] 기존 평문 저장 계정 마이그레이션 처리 — "최초 로그인 시 재해시" 로직 또는 일괄 마이그레이션 스크립트
- [ ] `AccountRow.cs` 및 `schema_game.sql` 주석 업데이트 ("Phase 3: BCrypt 해시")
- [ ] 부하 테스트: BCrypt workFactor=11 기준 ~100ms/hash — 동시 Register 처리량 측정 후 workFactor 조정 검토
- [ ] `AccountDbSet.InsertAsync` 반환을 `Task<ulong>`(삽입 ID)으로 변경해 SELECT 추가 왕복 제거 검토
- [ ] (선택) TLS 도입 — DotNetty `TlsHandler` 또는 역방향 프록시(nginx) 경유
- [ ] (선택) Rate Limiting — 동일 IP에서 Register/Login 다량 요청 제한 (brute force 방어)
