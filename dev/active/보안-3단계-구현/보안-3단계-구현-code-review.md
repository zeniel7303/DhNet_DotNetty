# 보안 3단계 구현 — 전체 코드 리뷰 (Phase 3 전 최종 점검)
Last Updated: 2026-03-20

## 총평

Phase 2 기반의 전체 코드베이스는 구조적으로 안정적이며 Phase 3(BCrypt) 전환을 위한 준비가 잘 되어 있다. 서버 코어, DB 레이어, 프로토콜, 클라이언트 시나리오 전반을 점검한 결과 Critical 항목은 **0건**, 보안/동시성/에러 처리 관점에서의 Major 항목 **3건**, Minor 개선 제안 **5건**이다.

강점 요약:
- **인증 로직**: User Enumeration 방어(동일 에러 코드), TOCTOU 방어(INSERT IGNORE + rows_affected), 중복 요청 방어(CAS 플래그)가 모두 올바르게 구현됨
- **동시성**: SessionSystem 이벤트 큐 기반 단일 스레드 처리, Interlocked/CAS 기반 플래그, ConcurrentDictionary 일관 사용으로 race condition 방어가 견고함
- **에러 처리**: 모든 DB 실패 경로에서 에러 응답 전송 + return 보장, fire-and-forget Task의 예외 관측 처리, DisconnectAsync 최상위 catch 패턴이 완전함
- **Phase 3 전환점**: `LoginProcessor.cs:166`, `RegisterProcessor.cs:81`, `AccountRow.cs:9`에 교체 포인트 주석이 명확히 표시됨

---

## 1. 보안

### 1-1. 평문 비밀번호 — 네트워크 전송 및 DB 저장 (Phase 2 의도적 한계)

**위치**: `login.proto:9`, `register.proto:9`, `RegisterProcessor.cs:81`, `LoginProcessor.cs:167`, `AccountRow.cs:9`

`ReqRegister.password`와 `ReqLogin.password`가 평문으로 전송되며, `accounts.password_hash` 컬럼에도 평문이 저장된다. AES-GCM 암호화 레이어가 활성화되어 있으면 네트워크 구간은 보호되지만, DB 유출 시 모든 비밀번호가 노출된다. Phase 3에서 BCrypt 해시로 교체 예정이며 코드에 교체 포인트가 명시되어 있으므로 인지된 한계이다.

**영향**: DB 유출 시 전체 계정 비밀번호 즉시 노출. 외부 네트워크 노출 금지.

### 1-2. Timing Attack 위험 — Phase 3 전환 시 Username Enumeration 노출

**위치**: `LoginProcessor.cs:166-168`

```csharp
if (account == null || account.password_hash != password)
```

Phase 2에서는 평문 비교이므로 타이밍 차이가 무시할 수준이다. 그러나 Phase 3에서 BCrypt.Verify()로 교체 시, `account == null`인 경우 BCrypt 연산을 건너뛰면 존재하지 않는 username이 ~100ms 빠르게 응답하여 username 열거가 가능해진다.

**Phase 3 필수 패턴**:
```csharp
private static readonly string _dummyHash = BCrypt.HashPassword("dummy", workFactor: 11);

var hashToVerify = account?.password_hash ?? _dummyHash;
var valid = account != null
    && await Task.Run(() => BCrypt.Verify(password, hashToVerify));
```

### 1-3. User Enumeration 방어 — 현재 올바름

**위치**: `LoginProcessor.cs:165-175`

`account == null`과 `password_hash != password` 모두 동일한 `ErrorCode.InvalidCredentials`를 반환하여 username 존재 여부를 노출하지 않는다. 로그에도 `"인증 실패: {username}"`만 기록하며 실패 원인을 구분하지 않는다.

### 1-4. RegisterProcessor — username 존재 시 명시적 UsernameTaken 반환

**위치**: `RegisterProcessor.cs:64-72`

회원가입 시에는 `UsernameTaken` 에러를 반환하므로 username 존재 여부가 노출된다. 이는 회원가입 UX에서 일반적으로 허용되는 패턴이며, 로그인과 달리 보안 위험이 낮다.

### 1-5. Log Injection 점검

**위치**: `LoginProcessor.cs:158`, `RegisterProcessor.cs:25`, `GameLogger` 전반

`username`이 로그 메시지에 직접 삽입된다 (`$"계정 조회 실패: {username}"`). 공격자가 username에 개행 문자(`\n`)나 제어 문자를 삽입하면 로그 위변조가 가능하다. 단, `RegisterProcessor`에서 username을 4~16자로 제한하고 있으므로 Register 경로에서는 긴 페이로드 삽입이 제한된다. LoginProcessor의 `AuthenticateAsync`에서는 username 길이 제한 없이 바로 DB 조회를 수행하므로, 매우 긴 username이나 특수문자 포함 username이 로그에 기록될 수 있다.

**권고**: LoginProcessor에서도 username 길이/형식 검증을 수행하거나, 로깅 시 username을 sanitize (예: 제어문자 제거, 최대 길이 제한) 처리를 추가한다.

### 1-6. AES-GCM 암호화 레이어 — 올바른 구현

**위치**: `AesGcmCryptor.cs`, `AesGcmDecryptionHandler.cs`, `AesGcmEncryptionHandler.cs`, `EncryptionSettings.cs`

- Nonce: 12바이트 GCM 표준, `RandomNumberGenerator.Fill`로 매 패킷 생성 — nonce 재사용 방지
- Auth-tag: 16바이트(128-bit) — 변조 감지
- 키 검증: Base64 디코딩 후 16바이트(AES-128) 확인
- 복호화 실패 시 연결 즉시 해제 (`ctx.CloseAsync()`)
- 파이프라인 순서: framing-dec -> crypto-dec -> protobuf-decoder / protobuf-encoder -> crypto-enc -> framing-enc — 올바름

### 1-7. 비밀번호 길이 상한 — BCrypt 전환 시 재검토 필요

**위치**: `RegisterProcessor.cs:14-15`

현재 `MaxLength = 16`이다. BCrypt는 72바이트까지 입력을 받으므로 기술적으로는 상한을 더 높일 수 있다. 그러나 DoS 방어(BCrypt는 긴 입력에서도 동일 비용) 관점에서 적절한 상한(예: 72자)을 유지하는 것이 좋다. Phase 3 전환 시 상한을 72 이하로 유지하되, 16자는 일반 사용자에게 다소 짧을 수 있으므로 검토가 필요하다.

---

## 2. 동시성 / Race Condition

### 2-1. TrySetLoginStarted / TrySetRegisterStarted — CAS 패턴 올바름

**위치**: `SessionComponent.cs:46-49`, `GameServerHandler.cs:38-45`

`Interlocked.CompareExchange`로 중복 ReqLogin/ReqRegister 전송 시 LoginProcessor/RegisterProcessor의 병렬 실행을 방지한다. 첫 번째 호출만 true를 반환하며, DotNetty I/O 이벤트 루프가 단일 스레드이므로 `ChannelRead0` 자체는 직렬 실행이지만, `_ = ProcessAsync()` fire-and-forget이 ThreadPool에서 실행되므로 CAS가 필수적이다.

### 2-2. SessionSystem 이벤트 큐 — 단일 스레드 소비자 패턴

**위치**: `SessionSystem.cs:92-105`

전용 스레드(`Loop`)가 `ConcurrentQueue`에서 이벤트를 소비한다. 생산자(I/O 이벤트 루프, LoginProcessor 등)와 소비자(SessionSystem 스레드)가 분리되어 있으며, 모든 세션 상태 변경(`_sessions.TryAdd/TryRemove`, `AttachPlayer`, `SetEntryHandshakeCompleted`)이 단일 소비자 스레드에서 실행되므로 내부 일관성이 보장된다.

### 2-3. PlayerGameEnter — Disconnect 순서 경합 처리

**위치**: `SessionSystem.cs:159-185`, `SessionSystem.cs:195-225`

Disconnect와 PlayerGameEnter의 순서 경합을 양방향으로 처리한다:
- **Disconnect 먼저 → PlayerGameEnter**: `IsDisconnected == true` → `tcs.TrySetCanceled()` (line 168-172)
- **PlayerGameEnter 먼저 → Disconnect**: `IsEntryHandshakeCompleted == true` → `DisconnectForNextTick()` (line 215-218)
- **세션 미존재**: `_sessions.TryGetValue` 실패 → `tcs.TrySetCanceled()` (line 161-164)

세 경로 모두 플레이어 누수 없이 정리된다.

### 2-4. PlayerComponent.DisconnectAsync — lock(this) 패턴

**위치**: `PlayerComponent.cs:127-133`, `PlayerComponent.cs:181-188`

`lock(this)`로 Room/Lobby 참조를 원자적으로 캡처한 후 lock 외부에서 Disconnect를 호출한다. `OnDispose`에서도 동일한 lock으로 null 처리를 수행한다. 교차 락 방지를 위해 lock 내부에서 외부 컴포넌트를 호출하지 않는 올바른 패턴이다.

다만 `lock(this)`는 외부에서 동일 객체로 lock을 걸 수 있는 이론적 위험이 있다. 현재 코드베이스에서 외부 lock 사용은 없으므로 실질적 문제는 아니지만, `private readonly object _lock = new()` 패턴이 더 안전하다.

### 2-5. LoginProcessor — DB await 후 연결 해제 확인

**위치**: `LoginProcessor.cs:64-69`

DB InsertAsync 완료 후 `session.IsDisconnected`를 확인하여 DB await 중 연결이 해제된 경우를 처리한다. `player.ImmediateFinalize()`로 즉시 정리하며, PlayerSystem 미등록 상태이므로 DB UpdateLogout도 불필요하다 (`_dbInserted`는 설정됨 → ImmediateFinalize → DisconnectAsync에서 UpdateLogout 실행). 이는 의도된 동작이다: InsertAsync 완료 후이므로 UpdateLogout이 실행되어야 DB 정합성이 유지된다.

### 2-6. RegisterProcessor — SELECT → INSERT TOCTOU 방어

**위치**: `RegisterProcessor.cs:48-103`

SELECT로 중복 확인 후 INSERT하는 패턴에서 발생 가능한 TOCTOU를 `INSERT IGNORE` + `rows_affected == 0` 검사로 DB 레벨에서 방어한다. `UNIQUE KEY ux_accounts_username`이 최종 중복 방어 역할을 수행한다.

---

## 3. 에러 처리

### 3-1. DB 실패 경로 — 모든 경로에서 에러 응답 보장

`LoginProcessor.AuthenticateAsync`와 `RegisterProcessor.ProcessAsync` 모두 DB 호출을 try-catch로 감싸고, catch에서 `ErrorCode.DbError` 응답을 전송한 후 return한다. 클라이언트가 응답 없이 대기하는 경로가 없다.

- `LoginProcessor.cs:152-164` — 계정 조회 실패 → DbError
- `LoginProcessor.cs:40-59` — 플레이어 Insert 실패 → DbError
- `RegisterProcessor.cs:49-62` — 중복 확인 실패 → DbError
- `RegisterProcessor.cs:75-93` — 계정 Insert 실패 → DbError
- `RegisterProcessor.cs:106-119` — account_id 재조회 실패 → DbError

### 3-2. Fire-and-forget Task 예외 관측

**위치**: `LoginProcessor.cs:135-141`, `SessionComponent.cs:62-66`, `GameServerHandler.cs:58-62`

`.FireAndForget("Login")` 패턴과 `.ContinueWith(OnlyOnFaulted)` 패턴으로 fire-and-forget Task의 예외를 관측하여 `UnobservedTaskException`을 방지한다.

### 3-3. DisconnectAsync 최상위 catch

**위치**: `PlayerComponent.cs:161-166`

`DisconnectAsync`가 fire-and-forget(`_ = DisconnectAsync()`)으로 호출되므로, 최상위 try-catch로 모든 예외를 잡아 로그에 기록한다. unhandled Task 예외가 프로세스를 크래시시키는 것을 방지한다.

### 3-4. SessionSystem ProcessEvent — 개별 이벤트 try-catch

**위치**: `SessionSystem.cs:111-139`

각 이벤트를 개별 try-catch로 감싸서 하나의 이벤트 처리 실패가 전체 이벤트 루프를 중단시키지 않는다.

### 3-5. AesGcmDecryptionHandler — CloseAsync 미관측 Task

**위치**: `AesGcmDecryptionHandler.cs:47`

```csharp
ctx.CloseAsync();
```

`CloseAsync()`의 반환 Task가 관측되지 않는다. `GameServerHandler.ExceptionCaught`(line 58-62)에서는 `ContinueWith(OnlyOnFaulted)`로 처리하지만, `AesGcmDecryptionHandler`에서는 누락되어 있다. CloseAsync 실패 시 `UnobservedTaskException`이 발생할 수 있다.

---

## 4. 아키텍처

### 4-1. 책임 분리 — 올바른 계층 구조

- **GameServerHandler**: 연결/해제 이벤트 + 패킷 라우팅만 담당 (단일 책임)
- **LoginProcessor / RegisterProcessor**: 인증/가입 로직 전담 (static 클래스)
- **SessionSystem**: 세션 생명주기 관리 (이벤트 큐 기반)
- **PlayerSystem**: 플레이어 등록/제거 + WorkerSystem 위임
- **PlayerComponent**: 패킷 드레인 + 라우터 테이블 기반 처리
- **AccountDbSet / PlayerDbSet**: 순수 데이터 접근 (SQL + Dapper)

각 레이어의 의존성 방향이 상위(Handler) → 중간(System) → 하위(Database)로 일방향이다.

### 4-2. RegisterProcessor — INSERT 후 SELECT 왕복 비효율

**위치**: `RegisterProcessor.cs:105-129`

INSERT 성공 후 `SelectByUsernameAsync`로 `account_id`를 재조회한다. MySQL의 `LAST_INSERT_ID()` 또는 Dapper의 `ExecuteScalarAsync`로 INSERT와 동시에 ID를 반환받으면 DB 왕복을 1회 줄일 수 있다. 현재 트래픽 수준에서는 병목이 아니지만, Phase 3에서 BCrypt 해싱 비용이 추가되면 전체 Register 지연이 증가하므로 함께 개선하는 것이 좋다.

### 4-3. LoginProcessor — username 입력 검증 부재

**위치**: `LoginProcessor.cs:149`

`RegisterProcessor`는 username 길이(4~16자)를 검증하지만, `LoginProcessor.AuthenticateAsync`에서는 username에 대한 사전 검증 없이 바로 DB 조회를 수행한다. 매우 긴 username(수천 자)이나 빈 문자열이 DB 쿼리로 직접 전달된다. Dapper가 파라미터 바인딩을 사용하므로 SQL Injection 위험은 없지만, 불필요한 DB 부하와 로그 오염이 발생할 수 있다.

### 4-4. 클라이언트 시나리오 — Register/Login 흐름 중복

**위치**: `BaseRoomScenario.cs:20-65`, `ReconnectStressScenario.cs:40-93`

`BaseRoomScenario`와 `ReconnectStressScenario`에서 Register → Login 흐름(ResRegister 수신 → SUCCESS/USERNAME_TAKEN 분기 → ReqLogin 전송)이 거의 동일하게 중복된다. `ReconnectStressScenario`가 `BaseRoomScenario`를 상속하지 않고 `ILoadTestScenario`를 직접 구현하기 때문이다. 기능적 문제는 아니지만, 인증 흐름 변경 시 두 곳을 동시에 수정해야 하는 유지보수 부담이 있다.

### 4-5. GamePipelineInitializer — 암호화 키 전체 연결 공유

**위치**: `GamePipelineInitializer.cs:13-17`

`_encKey`가 생성자에서 한 번 설정되어 모든 연결의 `AesGcmEncryptionHandler`/`AesGcmDecryptionHandler`에 동일 키가 전달된다. Pre-shared key 방식의 의도된 설계이며, `EncryptionSettings`에 근거가 문서화되어 있다. 향후 세션별 키 교환(ECDH)으로 업그레이드할 경우 이 구조를 변경해야 한다.

---

## 5. Phase 3 준비도 및 체크리스트

### Phase 3 변경 필요 지점

| 파일 | 위치 | 변경 내용 |
|------|------|-----------|
| `RegisterProcessor.cs` | line 81 | `password` → `await Task.Run(() => BCrypt.HashPassword(password, 11))` |
| `LoginProcessor.cs` | line 166-168 | 평문 비교 → `BCrypt.Verify` + dummy hash 타이밍 균일화 |
| `AccountRow.cs` | line 9 | 주석 업데이트 ("Phase 3: BCrypt 해시") |
| `schema_game.sql` | line 32 | 주석 업데이트 |
| `RegisterProcessor.cs` | line 14-15 | `MaxLength` 재검토 (16 → 72 범위 내 적절한 값) |
| (선택) `LoginProcessor.cs` | 새 코드 | username 길이/형식 사전 검증 추가 |

### Phase 3 구현 체크리스트

- [ ] `BCrypt.Net-Next` NuGet 패키지 추가 (GameServer 프로젝트)
- [ ] `RegisterProcessor.cs:81` — `password_hash = await Task.Run(() => BCrypt.HashPassword(password, 11))`
- [ ] `LoginProcessor.AuthenticateAsync` — dummy hash 패턴 적용 (타이밍 균일화)
  ```csharp
  private static readonly string _dummyHash = BCrypt.HashPassword("dummy", workFactor: 11);
  var hashToVerify = account?.password_hash ?? _dummyHash;
  var valid = account != null && await Task.Run(() => BCrypt.Verify(password, hashToVerify));
  ```
- [ ] 기존 평문 계정 마이그레이션 — 2가지 전략 중 선택:
  - (A) 최초 로그인 시 재해시: 평문 일치 확인 후 BCrypt 해시로 UPDATE
  - (B) 일괄 마이그레이션 SQL 스크립트 (서버 다운타임 중 실행)
- [ ] `RegisterProcessor.MaxLength` 재검토 — 16자 → 64자 등 적절한 상한
- [ ] LoginProcessor에 username 길이/형식 사전 검증 추가
- [ ] AccountRow, schema_game.sql 주석 업데이트
- [ ] 부하 테스트: BCrypt workFactor=11 기준 Register/Login TPS 측정
- [ ] `AccountDbSet.InsertAsync` → `LAST_INSERT_ID()` 패턴으로 SELECT 왕복 제거 검토
- [ ] (선택) AesGcmDecryptionHandler.ExceptionCaught의 CloseAsync 미관측 Task 수정
- [ ] (선택) Rate Limiting — Register/Login 다량 요청 제한 (brute force 방어)
- [ ] (선택) `lock(this)` → `private readonly object _lock = new()` 패턴 전환

### Phase 3 전환 시 주의사항

1. **BCrypt는 CPU 집약적**: `Task.Run`으로 ThreadPool 실행 필수. DotNetty I/O 이벤트 루프나 SessionSystem 스레드에서 직접 실행하면 전체 서버 처리량이 급감한다.
2. **타이밍 균일화 필수**: account가 null일 때도 dummy hash로 BCrypt.Verify를 실행해야 Username Enumeration을 방지할 수 있다.
3. **마이그레이션 호환성**: 기존 평문 계정과 새 BCrypt 해시 계정이 혼재하는 과도기를 처리해야 한다. password_hash 값이 `$2a$` 또는 `$2b$`로 시작하는지 여부로 판별 가능하다.
4. **workFactor 선택**: 11이면 약 100~200ms/hash. 동시 Register 요청이 많으면 ThreadPool 포화 가능. 부하 테스트 후 결정한다.
5. **클라이언트 변경 없음**: 프로토콜(login.proto, register.proto)에서 password 필드는 평문 그대로 전송하며, 해싱은 서버에서만 수행하므로 클라이언트 수정이 불필요하다.

---

## 수정 권고 (Critical / Major / Minor)

### Critical — 없음

### Major

| ID | 항목 | 위치 | 설명 |
|----|------|------|------|
| M-1 | 평문 비밀번호 DB 저장 | `RegisterProcessor.cs:81` | Phase 3에서 BCrypt 해시로 교체 필수. 현재 DB 유출 시 전체 비밀번호 노출. |
| M-2 | Timing Attack 위험 | `LoginProcessor.cs:166` | Phase 3 전환 시 dummy hash 패턴 적용 필수. 누락 시 Username Enumeration 가능. |
| M-3 | LoginProcessor username 검증 부재 | `LoginProcessor.cs:149` | 길이 제한 없이 DB 조회. 긴 입력으로 불필요한 DB 부하 및 로그 오염 발생 가능. |

### Minor

| ID | 항목 | 위치 | 설명 |
|----|------|------|------|
| m-1 | AesGcmDecryptionHandler CloseAsync 미관측 | `AesGcmDecryptionHandler.cs:47` | `_ = ctx.CloseAsync()` 또는 ContinueWith(OnlyOnFaulted) 추가 권고 |
| m-2 | lock(this) 패턴 | `PlayerComponent.cs:129,183` | `private readonly object _lock = new()` 전용 락 객체 사용 권고 |
| m-3 | INSERT 후 SELECT 왕복 | `RegisterProcessor.cs:105-129` | `LAST_INSERT_ID()` 패턴으로 DB 왕복 1회 절감 가능 |
| m-4 | 클라이언트 Register/Login 흐름 중복 | `BaseRoomScenario.cs`, `ReconnectStressScenario.cs` | 인증 흐름을 공통 유틸로 추출하면 유지보수성 향상 |
| m-5 | 비밀번호 MaxLength 재검토 | `RegisterProcessor.cs:15` | 현재 16자. Phase 3에서 사용자 편의를 위해 64자 정도로 확대 검토 |
