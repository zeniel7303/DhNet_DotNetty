# 패킷 암호화 AES-GCM — 아키텍처 코드 리뷰

Last Updated: 2026-03-20

---

# Phase 1 (AES-GCM 파이프라인) 코드 리뷰

<details>
<summary>접기/펼치기</summary>

## 총평

전체적으로 구현 수준이 높다. AES-GCM 알고리즘 선택, Nonce 처리, DotNetty 파이프라인 통합, 예외 처리까지
핵심 판단들이 올바르다. 아래에서는 정확히 맞는 부분과 개선이 필요한 부분을 구분하여 기술한다.

---

## 1. DotNetty 파이프라인 순서 정확성

### 판정: 올바름 (단, 한 가지 주의사항 있음)

#### 등록 순서 (서버/클라 동일)
```
pipeline.AddLast("framing-enc",     new LengthFieldPrepender(2));       // 아웃바운드
pipeline.AddLast("framing-dec",     new LengthFieldBasedFrameDecoder()); // 인바운드
pipeline.AddLast("crypto-dec",      new AesGcmDecryptionHandler());      // 인바운드
pipeline.AddLast("crypto-enc",      new AesGcmEncryptionHandler());      // 아웃바운드
pipeline.AddLast("protobuf-decoder",new ProtobufDecoder());              // 인바운드
pipeline.AddLast("protobuf-encoder",new ProtobufEncoder());              // 아웃바운드
pipeline.AddLast("handler",         new GameServerHandler());
```

#### 인바운드 실제 처리 경로 (wire -> handler)
```
wire -> LengthFieldBasedFrameDecoder -> AesGcmDecryptionHandler -> ProtobufDecoder -> handler
```
올바름. 프레이밍 분리 후 복호화, 복호화 후 protobuf 파싱 순서가 맞다.

#### 아웃바운드 실제 처리 경로 (handler -> wire)
DotNetty 아웃바운드는 AddLast 역순으로 처리된다.
```
handler -> ProtobufEncoder -> AesGcmEncryptionHandler -> LengthFieldPrepender -> wire
```
올바름. protobuf 직렬화 -> 암호화 -> length 헤더 부착 순서가 맞다.

#### 주의사항: `crypto-dec`와 `crypto-enc`의 AddLast 순서
`crypto-dec`(인바운드)와 `crypto-enc`(아웃바운드)는 서로 다른 방향 핸들러이므로 등록 순서가
실제 동작에 영향을 미치지 않는다. 그러나 `framing-dec`와 `framing-enc` 사이에 인터리빙되어
있는 구조가 처음 보는 독자에게는 혼란을 줄 수 있다.

권장 방향: 아래처럼 인/아웃 쌍을 붙여 주석으로 방향을 명시하면 유지보수성이 높아진다.
```csharp
// 현재 코드에서 주석으로 방향이 이미 기술되어 있어 실용적으로 충분함
```

---

## 2. IByteBuffer 참조 카운팅 (메모리 누수 가능성)

### 판정: 정상 처리됨 (단, 경계 조건 버그 존재)

#### 정상 경로
`MessageToMessageDecoder<IByteBuffer>` 및 `MessageToMessageEncoder<IByteBuffer>`의 부모 클래스는
`channelRead`/`write` 처리 후 입력 `msg`의 `Release()`를 자동으로 호출한다. 이는 DotNetty의
`MessageToMessageDecoder` 계약이며 코드에서 직접 Release를 호출하지 않아도 된다.

출력 버퍼(`ctx.Allocator.Buffer()`)는 `output.Add(buf)` 후 다음 핸들러에게 소유권이 넘어가므로
현재 핸들러에서 Release 불필요하다. 올바르다.

#### 버그: 예외 발생 시 출력 버퍼 누수 (DecryptionHandler)

**파일**: `GameServer/Network/AesGcmDecryptionHandler.cs`, 31-40행
**파일**: `GameClient/Network/AesGcmDecryptionHandler.cs`, 25-33행

```csharp
protected override void Decode(IChannelHandlerContext ctx, IByteBuffer msg, List<object> output)
{
    var encrypted = new byte[msg.ReadableBytes];
    msg.ReadBytes(encrypted);

    var decrypted = AesGcmCryptor.Decrypt(_key, encrypted);  // <- 예외 발생 가능

    var buf = ctx.Allocator.Buffer(decrypted.Length);  // <- Decrypt 성공 시에만 도달
    buf.WriteBytes(decrypted);
    output.Add(buf);
}
```

`AesGcmCryptor.Decrypt`가 `AuthenticationTagMismatchException` 또는
`CryptographicException`을 던지면 `buf`는 할당되지 않으므로 누수가 없다.
즉, 이 경로는 현재 코드에서 실제로 문제가 없다.

단, 미래에 `buf` 할당 후 `buf.WriteBytes()` 단계에서 예외가 발생하는 경우를 위해
방어적으로 try/finally 패턴을 갖추는 것이 더 견고하다:

```csharp
// 권고 패턴 (현재 코드는 문제없으나 방어적 개선)
var buf = ctx.Allocator.Buffer(decrypted.Length);
bool added = false;
try
{
    buf.WriteBytes(decrypted);
    output.Add(buf);
    added = true;
}
finally
{
    if (!added) buf.Release();
}
```

현재 코드 수준에서 실제 누수는 없다. 중요도: 낮음.

#### EncryptionHandler: 정상

`AesGcmEncryptionHandler.Encode`도 동일 패턴이며, `Encrypt`가 예외를 던지면 `buf` 미할당으로
누수 없음. 단, `Encrypt` 내부에서 `RandomNumberGenerator.Fill`이나 `aes.Encrypt`가 실패하면
`ExceptionCaught`가 호출된다. `AesGcmEncryptionHandler`에는 `ExceptionCaught` 오버라이드가 없어
예외가 파이프라인 상위로 전파된다. 치명적이진 않지만 로그 없이 연결이 끊어질 수 있다.

---

## 3. AES-GCM 구현 정확성

### 판정: 정확함

#### Nonce 처리
- 12바이트(96-bit): GCM 표준 권장 크기. 올바름.
- `RandomNumberGenerator.Fill(nonce)`: 패킷마다 CSPRNG로 새 Nonce 생성. Nonce 재사용 방지.
  GCM의 가장 치명적인 약점(Nonce 재사용 시 키 복구 가능)을 올바르게 차단하고 있다.
- Wire 포맷 `[12B nonce | ciphertext | 16B auth-tag]`에 Nonce가 평문으로 포함된다.
  GCM에서 Nonce는 비밀이 아니어도 되므로 올바른 설계다.

#### 태그 처리
- 16바이트(128-bit) 인증 태그: GCM 최대 크기. `new AesGcm(key, TagSize)` 명시적 지정. 올바름.
- `aes.Decrypt`는 태그 불일치 시 `AuthenticationTagMismatchException`을 자동으로 던진다.
  변조/재전송 패킷 감지가 자동으로 이루어진다.

#### 키 유효성 검증
- `EncryptionSettings.GetKeyBytes()`에서 16바이트 정확성 검증. 올바름.
- 서버 시작 시 1회 검증되므로 런타임에 잘못된 키로 AesGcm 인스턴스가 생성되는 경우는 없다.

#### AesGcm 인스턴스 생성 방식
**파일**: `Common.Shared/Crypto/AesGcmCryptor.cs`, 62행 / 84행

```csharp
using var aes = new AesGcm(key, TagSize);
```

`using`으로 즉시 생성 및 해제. 패킷마다 인스턴스를 생성하는 방식이다.
`.NET`의 `AesGcm`은 내부적으로 AES-NI 키 스케줄을 캐싱하므로 생성 비용이 낮지만,
고빈도 환경에서는 정적 인스턴스 재사용이 성능상 이점이 있다.

현재 방식의 문제: 부하 테스트 1000명 시나리오에서 패킷당 두 번(Encrypt/Decrypt)
`AesGcm` 인스턴스가 생성된다. GC 압박이 미미하게 증가한다.

대안: `[ThreadStatic]` 또는 `ThreadLocal<AesGcm>` 정적 인스턴스 풀링.
단, `AesGcm`은 `IDisposable`이므로 풀링 시 생명주기 관리 복잡도가 증가한다.
현재 규모에서는 교체 필요성 없음. 중요도: 낮음.

---

## 4. 예외 처리 및 오류 복구 경로

### 판정: 서버 DecryptionHandler는 적절함, EncryptionHandler는 누락

#### 서버 AesGcmDecryptionHandler -- 적절함
```csharp
public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
{
    GameLogger.Warn("Crypto", $"복호화 실패 ({ctx.Channel.RemoteAddress}): {ex.Message}");
    ctx.CloseAsync();
}
```
- 로그 후 연결 해제: 올바름. 변조 패킷을 수신한 세션을 즉시 격리한다.
- `CloseAsync()`의 반환 Task를 `await`하지 않는다. 이는 DotNetty `ExceptionCaught`의
  일반적 패턴으로 허용된다. 단, `CloseAsync()`가 실패해도 예외가 묵살된다.
  현재 패턴은 프로젝트 전반에서 일관되게 사용되므로 문제없다.

#### 서버/클라 AesGcmEncryptionHandler -- ExceptionCaught 없음

`AesGcmEncryptionHandler`에는 `ExceptionCaught`가 구현되어 있지 않다.
암호화 중 예외 발생 시 파이프라인 상위(`GameServerHandler`)로 전파된다.
`GameServerHandler`에 `ExceptionCaught`가 있다면 처리되지만, 암호화 레이어에서
발생한 오류임을 구분하는 로그가 없어 디버깅이 어려울 수 있다.

권고: `AesGcmEncryptionHandler`에도 `ExceptionCaught` 추가.
중요도: 낮음 (런타임 오류 발생 가능성이 Decrypt보다 훨씬 낮음).

#### 클라 DecryptionHandler -- 적절함
서버와 동일 패턴. 로그 + CloseAsync. 올바름.

#### 클라 EncryptionHandler -- 서버와 동일 이슈 (ExceptionCaught 없음)

---

## 5. 잠재적 버그 및 엣지 케이스

### 5.1 [버그] LoadTestConfig -- Base64 디코딩 예외 미처리

**파일**: `GameClient/Program.cs`, 125-127행 및 202-204행

`--encryption-key`에 잘못된 Base64 문자열을 입력하면 `FormatException`이 발생한다.
이 예외는 `ConnectClientAsync`의 외부 `catch (Exception ex)` 블록에서 잡히지만,
연결 오류(`LoadTestStats.IncrementErrors()`)로 집계된다. 실제로는 설정 오류인데
1000개 클라이언트가 모두 "연결 실패"로 보고하는 혼란스러운 상황이 발생한다.

권고: 클라이언트 시작 시점(Program.RunAsync)에서 한 번 검증.
중요도: 중간 (부하 테스트 시나리오에서 잘못된 키로 1000개 오류 발생 가능).

### 5.2 [설계] Pre-shared Key 방식의 구조적 한계 (문서에 명시됨)

Pre-shared Key는 서버와 클라이언트가 동일한 키를 가진다는 의미다.
- 클라이언트 바이너리가 노출되면 키도 노출된다.
- 모든 클라이언트가 동일 키를 사용하므로, 한 세션의 트래픽을 탈취해도
  다른 세션의 이전 트래픽을 복호화할 수 있다 (Forward Secrecy 없음).
- 이는 `EncryptionSettings.cs` 주석에 "Phase 1, ECDH로 교체 예정"으로 명시되어 있다.
  현재 단계에서의 의도적 트레이드오프로 허용 가능.

### 5.3 [설계] 키 재사용 없음 확인 (올바름)

`AesGcmCryptor.Encrypt`는 호출마다 `RandomNumberGenerator.Fill`로 새 12바이트 Nonce를
생성한다. 동일 키로 동일 Nonce가 재사용되는 GCM의 치명적 약점이 올바르게 차단되어 있다.

12바이트 랜덤 Nonce의 충돌 확률은 약 2^96 분의 1이며, 하나의 세션에서 전송되는 패킷 수
(수천~수십만 개)에서는 충돌이 사실상 불가능하다.

### 5.4 [설계] 암호화/비암호화 혼용 불가

서버와 클라이언트 모두 Key 설정 여부에 따라 파이프라인에 crypto 핸들러가 추가/제외된다.
서버가 암호화 활성화 상태에서 암호화 미적용 클라이언트가 연결하면:
- 클라이언트가 보낸 평문이 서버의 `AesGcmDecryptionHandler`에 도달
- Nonce/태그 길이 부족 또는 auth-tag 불일치로 `CryptographicException`
- `ExceptionCaught` -> 연결 해제

예상대로 동작함. 단, 로그 메시지가 "복호화 실패"로만 남아 운영 시 원인 진단이 모호할 수 있다.

### 5.5 [관찰] 서버/클라 핸들러 코드 중복

서버와 클라의 `AesGcmDecryptionHandler`, `AesGcmEncryptionHandler`는 코드가 완전히 동일하다.
차이는 네임스페이스(`GameServer.Network` vs `GameClient.Network`)뿐이다.

context.md에 "Common.Shared에 DotNetty.Codecs 추가보다 2파일 복사가 더 단순"이라는
의도적 결정이 기록되어 있다. 현재 규모에서 합리적인 판단이다.

다만 향후 ECDH 핸드쉐이크 등 세션별 키 관리가 도입될 경우, 두 파일을 동시에 수정해야 하는
유지보수 부담이 생긴다. 그 시점에 `Common.Shared`에 DotNetty.Codecs 의존성 추가를 재검토할 것.

---

## 6. 종합 평가

| 항목 | 평가 | 비고 |
|------|------|------|
| AES-GCM 알고리즘 선택 | 우수 | AES-NI 활용, AEAD 단일 패스 |
| Nonce 처리 | 우수 | 패킷마다 CSPRNG, 재사용 없음 |
| 인증 태그 처리 | 우수 | 16바이트, 변조 자동 감지 |
| DotNetty 파이프라인 순서 | 정확 | 인/아웃바운드 모두 올바름 |
| IByteBuffer 참조 카운팅 | 정상 | 자동 Release 계약 준수 |
| DecryptionHandler 예외 처리 | 적절 | 로그 + CloseAsync |
| EncryptionHandler 예외 처리 | 미비 | ExceptionCaught 없음 (낮은 위험도) |
| LoadTestConfig Base64 검증 | 결함 | 시작 시점 조기 검증 없음 (중간 위험도) |
| AesGcm 인스턴스 생성 비용 | 수용 가능 | 패킷마다 생성, 부하 테스트 규모에서 문제없음 |
| 비활성화 모드 | 적절 | Key="" 로 핸들러 생략, 경고 로그 출력 |

### 즉시 수정 권고 (중간 우선순위)
1. `GameClient/Program.cs` -- `ConnectClientAsync` 및 `RunReconnectLoopAsync` 두 곳에서
   `Convert.FromBase64String(config.EncryptionKey)` 호출 전에 Base64 유효성 사전 검증 추가.

### 선택적 개선 (낮은 우선순위)
2. `AesGcmEncryptionHandler` (서버/클라) -- `ExceptionCaught` 오버라이드 추가.
3. `AesGcmDecryptionHandler.Decode` -- `buf` 할당 후 방어적 try/finally 패턴 추가.

---

## 7. 참조 파일 경로

- `E:/MyProject/DhNet_DotNetty/Common.Shared/Crypto/AesGcmCryptor.cs`
- `E:/MyProject/DhNet_DotNetty/Common.Server/EncryptionSettings.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/AesGcmDecryptionHandler.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/AesGcmEncryptionHandler.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/GamePipelineInitializer.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/GameServerBootstrap.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/ServerStartup.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Network/AesGcmDecryptionHandler.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Network/AesGcmEncryptionHandler.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/LoadTestConfig.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Program.cs`

</details>

---

# Phase 2 (회원가입/비밀번호 인증) 코드 리뷰

Last Updated: 2026-03-20

## 총평

Phase 2 구현은 전체적으로 견고하다. 핵심 흐름(Register -> Login -> PlayerGameEnter -> LobbyEnter)이 올바르게 연결되어 있고, 중복 방지 CAS 플래그(`TrySetRegisterStarted`/`TrySetLoginStarted`), DB 실패 경로의 에러 응답 보장, INSERT IGNORE + rows_affected 이중 체크, User Enumeration 방지 등 보안 및 동시성 관점의 핵심 판단들이 적절하다. Phase 3(BCrypt) 전환 포인트도 주석으로 명확히 표시되어 있어 마이그레이션 준비도가 높다.

다만 몇 가지 보안 및 안정성 관련 이슈가 발견되었다. 아래에서 항목별로 기술한다.

---

## 보안

### 2.1 [Critical] 평문 비밀번호 저장

**파일**: `GameServer/Network/RegisterProcessor.cs`, 81행

```csharp
password_hash = password,  // Phase 3에서 BCrypt.HashPassword(password, 11)로 교체
```

`password_hash` 컬럼에 평문 비밀번호가 그대로 저장된다. DB 유출 시 모든 계정의 비밀번호가 즉시 노출된다. Phase 3에서 BCrypt로 교체 예정이라는 주석이 있으며, DB 컬럼명(`password_hash`)과 Row 클래스 필드명이 이미 해시 기반으로 설계되어 있어 전환 시 스키마 변경이 불필요하다.

**평가**: Phase 2의 의도적 결정으로 명시되어 있으므로 현 단계에서는 허용. 단, Phase 3 전까지 프로덕션 배포 불가.

### 2.2 [Good] User Enumeration 방지

**파일**: `GameServer/Network/LoginProcessor.cs`, 166-174행

```csharp
if (account == null || account.password_hash != password)
{
    // ...
    ResLogin = new ResLogin { ErrorCode = ErrorCode.InvalidCredentials }
}
```

username 미존재와 password 불일치를 동일한 `INVALID_CREDENTIALS` 에러로 응답한다. 공격자가 응답 코드만으로 유효한 username을 식별할 수 없다. 올바른 보안 패턴이다.

### 2.3 [Good] SQL Injection 방지

**파일**: `GameServer.Database/DbSet/AccountDbSet.cs`

모든 SQL 쿼리에서 Dapper 파라미터 바인딩(`@username`, `@password_hash`)을 사용한다. 문자열 결합 기반 쿼리 구성은 없다. SQL Injection 위험 없음.

### 2.4 [Minor] 로그에 username 포함

**파일**: `GameServer/Network/RegisterProcessor.cs`, 25행

```csharp
GameLogger.Warn("Register", $"username 길이 위반: username={username}, len={username.Length}");
```

검증 실패 시 입력된 username 원문이 로그에 기록된다. 악의적 사용자가 매우 긴 문자열이나 제어 문자를 username에 넣으면 로그 파일이 오염될 수 있다 (Log Injection). 현재 `MaxLength=16` 검증이 있어 길이 기반 공격은 차단되지만, 길이 검증 이전에 로그가 출력되므로 16자 초과 문자열이 로그에 기록될 수 있다.

**권고**: 로그 출력 시 username 길이를 잘라내거나 (`username[..Math.Min(username.Length, 32)]`), 혹은 검증 실패 로그에서 username 원문을 생략하고 길이만 기록.
**중요도**: Minor (운영 환경 로그 관리 정책에 따라 판단).

### 2.5 [Minor] password에 대한 Trim 미적용

**파일**: `GameServer/Network/RegisterProcessor.cs`, 19-20행

```csharp
var username = req.Username.Trim();
var password = req.Password;  // Trim 없음
```

username은 Trim하지만 password는 하지 않는다. 이는 올바른 판단이다. 비밀번호 앞뒤 공백은 사용자 의도일 수 있으므로 Trim하면 안 된다. 다만, 로그인 시 `LoginProcessor.AuthenticateAsync`에서도 password를 Trim 없이 비교하는지 확인이 필요하다.

**확인 결과**: `LoginProcessor.AuthenticateAsync`에서 `req.Password`를 그대로 사용 (`account.password_hash != password`). 일관성 있음. 올바르다.

### 2.6 [Observation] Register -> Login 순서 강제 없음

서버 측에서 Register를 먼저 호출하지 않고 바로 Login을 보내는 것이 가능하다. `GameServerHandler.ChannelRead0`에서 `ReqRegister`와 `ReqLogin`은 독립적인 case로 처리된다. 이미 계정이 존재하면 Register 없이 Login만 보내도 정상 로그인된다.

**평가**: 이는 올바른 설계다. Register는 최초 1회용이고, 기존 계정 사용자는 Login만 보내면 된다. 클라이언트 시나리오에서는 매번 Register -> Login 순서로 보내되, `USERNAME_TAKEN` 응답을 정상 흐름으로 처리한다.

---

## 동시성 / Race Condition

### 2.7 [Good] TrySetRegisterStarted / TrySetLoginStarted CAS 패턴

**파일**: `GameServer/Network/SessionComponent.cs`, 46-49행

```csharp
public bool TrySetRegisterStarted() => Interlocked.CompareExchange(ref _registerStarted, 1, 0) == 0;
public bool TrySetLoginStarted() => Interlocked.CompareExchange(ref _loginStarted, 1, 0) == 0;
```

DotNetty I/O 스레드에서 `ChannelRead0`가 호출될 때 동일 세션에서 중복 `ReqRegister`/`ReqLogin` 패킷이 빠르게 연속 도착하면 `RegisterProcessor.ProcessAsync`/`LoginProcessor.ProcessAsync`가 병렬 실행될 수 있다. CAS 패턴으로 첫 번째 호출만 허용하고 이후 호출은 무시한다. 올바르다.

**참고**: DotNetty의 채널 핸들러는 기본적으로 단일 EventLoop 스레드에서 실행되므로, 같은 채널의 `ChannelRead0`는 순차 호출된다. 따라서 이론적으로 CAS가 필수는 아니지만, `ProcessAsync`가 fire-and-forget(`_ = ProcessAsync(...)`)으로 호출되면서 첫 번째 ProcessAsync의 await 중에 두 번째 ChannelRead0가 실행될 수 있다. 이 경우 CAS가 효과적으로 중복을 차단한다. 방어적 설계로 적절하다.

### 2.8 [Good] INSERT IGNORE + rows_affected 이중 체크

**파일**: `GameServer/Network/RegisterProcessor.cs`, 48-103행

```
1. SELECT로 기존 username 존재 여부 확인 -> 빠른 에러 응답
2. INSERT IGNORE 실행 -> 동시 요청에 의한 중복도 안전하게 처리
3. rows_affected == 0 -> 동시 요청에 의한 중복 (INSERT IGNORE가 무시한 경우)
```

Race condition 시나리오: 두 클라이언트가 동시에 같은 username으로 Register 요청.
- 클라이언트 A: SELECT -> null -> INSERT IGNORE -> rows_affected=1 (성공)
- 클라이언트 B: SELECT -> null -> INSERT IGNORE -> rows_affected=0 (중복, USERNAME_TAKEN 응답)

INSERT IGNORE가 DB 레벨에서 UNIQUE 제약 위반 시 예외 대신 0을 반환하므로 안전하다. 올바른 패턴이다.

### 2.9 [Minor] Register 후 SELECT 재조회 시 null 가능성

**파일**: `GameServer/Network/RegisterProcessor.cs`, 109-121행

```csharp
created = await DatabaseSystem.Instance.Game.Accounts.SelectByUsernameAsync(username);
// ...
GameLogger.Info("Register", $"계정 생성 완료: {username} (account_id={created!.account_id})");
```

INSERT 성공(rows_affected=1) 후 SELECT로 account_id를 조회한다. `created!`로 null 아님을 단언하고 있다. INSERT 직후에 SELECT하므로 정상적인 상황에서 null이 될 수 없지만, 극히 드문 케이스(INSERT 직후 다른 프로세스가 DELETE, 또는 MySQL replication lag)에서는 null이 될 수 있다.

**권고**: `created`가 null인 경우 DB_ERROR 응답을 보내는 방어 코드 추가.
```csharp
if (created == null)
{
    GameLogger.Error("Register", $"INSERT 성공 후 SELECT 실패: {username}");
    await session.SendAsync(...DbError...);
    return;
}
```
**중요도**: Minor (단일 DB 인스턴스에서는 사실상 발생 불가).

### 2.10 [Observation] Register와 Login의 독립적 CAS 플래그

`_registerStarted`와 `_loginStarted`는 서로 독립적이다. 이론적으로 한 세션에서 Register 처리 중에 Login 요청이 도착하면 둘 다 병렬 실행된다. Register 완료 전에 Login이 먼저 완료되면:
- Register: 계정이 이미 생성됨 -> SELECT에서 existing != null -> USERNAME_TAKEN 응답
- Login: 계정이 존재하고 password 일치 -> 로그인 성공

이 경우 클라이언트는 USERNAME_TAKEN과 로그인 성공 응답을 동시에 받는다. 클라이언트 시나리오에서는 USERNAME_TAKEN을 정상 흐름으로 처리하므로 문제가 되지 않으나, Register와 Login을 동시에 보내는 악의적 클라이언트에 대한 방어가 없다.

**평가**: 현재 클라이언트 시나리오에서는 Register 응답을 받은 후 Login을 보내므로 실제로 발생하지 않는다. 향후 방어가 필요하면 `_registerStarted`와 `_loginStarted`를 하나의 상태 머신(`_authState: None -> Registering -> LoginReady -> LoggingIn -> LoggedIn`)으로 통합하는 것이 좋다.
**중요도**: Minor (현재 클라이언트 흐름에서 발생 불가).

---

## 에러 처리

### 2.11 [Good] 모든 DB 호출에 try/catch + 에러 응답

**파일**: `GameServer/Network/RegisterProcessor.cs`, `GameServer/Network/LoginProcessor.cs`

RegisterProcessor에서 3개, LoginProcessor에서 2개의 DB 호출이 모두 개별 try/catch로 감싸져 있으며, 실패 시 `ErrorCode.DbError` 응답을 클라이언트에 전송한다. DB 장애 시 클라이언트가 무응답 상태에 빠지지 않는다. 올바르다.

### 2.12 [Good] LoginProcessor의 연결 해제 타이밍 처리

**파일**: `GameServer/Network/LoginProcessor.cs`, 64-69행

```csharp
player.MarkDbInserted();

if (session.IsDisconnected)
{
    player.ImmediateFinalize();
    return;
}
```

DB await 중 클라이언트가 연결을 끊은 경우를 처리한다. `MarkDbInserted()` 이후 `IsDisconnected` 체크로, DB에 이미 기록된 플레이어의 정리(logout_at 업데이트)가 `ImmediateFinalize`에서 수행된다. 올바른 타이밍이다.

### 2.13 [Good] OperationCanceledException 처리

**파일**: `GameServer/Network/LoginProcessor.cs`, 81-95행

```csharp
try { await tcs.Task; }
catch (OperationCanceledException) { /* 연결 해제 시 TrySetCanceled */ }
catch (Exception ex) { player.ImmediateFinalize(); }
```

`PlayerGameEnter` 대기 중 연결 해제 시 `OperationCanceledException`을 잡아 조용히 종료한다. 일반 예외 시에는 `ImmediateFinalize`로 정리한다. 올바르다.

### 2.14 [Minor] RegisterProcessor에서 fire-and-forget 예외 손실

**파일**: `GameServer/Network/GameServerHandler.cs`, 39행

```csharp
_ = RegisterProcessor.ProcessAsync(_session, packet.ReqRegister);
```

`ProcessAsync`가 fire-and-forget으로 호출된다. ProcessAsync 내부의 모든 예외는 try/catch로 처리되고 있으므로 미관측 Task 예외는 발생하지 않는다. 그러나 만약 향후 ProcessAsync에 try/catch 없는 경로가 추가되면 예외가 손실된다.

**평가**: 현재 코드에서는 문제없다. LoginProcessor도 동일 패턴이며, 둘 다 내부에서 모든 예외를 catch하고 있다.

---

## 프로토콜 설계

### 2.15 [Good] ErrorCode 대역 설계

**파일**: `GameServer.Protocol/Protos/error_codes.proto`

```
0        : SUCCESS
1000~1999: 시스템 오류 (서버 과부하, DB 등)
2000~2999: 로비 오류
3000~3999: 룸 오류
```

대역별 구분이 명확하다. 인증 관련 에러(1002~1005)가 시스템 대역에 배치되어 있는데, 이는 인증이 게임 로직이 아닌 시스템 레벨 기능이므로 적절한 분류다.

### 2.16 [Observation] ReqLogin.player_name과 accounts.username의 관계

**파일**: `GameServer.Protocol/Protos/login.proto`

```protobuf
message ReqLogin {
  string player_name = 1;
  string password    = 2;
}
```

로그인 요청 필드명이 `player_name`이지만 실제로는 `accounts.username`으로 인증에 사용된다. `LoginProcessor.AuthenticateAsync`에서 `req.PlayerName`을 `SelectByUsernameAsync`에 전달한다.

```csharp
var account = await AuthenticateAsync(session, req.PlayerName, req.Password);
```

그리고 로그인 성공 시 player_name은 DB의 `account.username`으로 덮어쓴다:

```csharp
var player = new PlayerComponent(session, account.username);
```

**평가**: 기능적으로 올바르게 동작하지만, proto 필드명 `player_name`이 실제 의미(`username`)와 다르다. 향후 player_name과 username이 분리되는 시점(닉네임 시스템 등)에서 혼란을 줄 수 있다. 현재 단계에서는 클라이언트/서버 모두 `player_name = username`으로 사용하므로 문제없다.

**권고**: proto 필드명을 `username`으로 변경하면 코드 가독성이 향상된다. 단, 기존 클라이언트 호환성을 고려하면 field number를 유지하고 필드명만 변경하면 wire 호환성에 영향이 없다 (protobuf는 field number 기반).
**중요도**: Minor (가독성 개선).

### 2.17 [Good] game_packet.proto oneof 확장

**파일**: `GameServer.Protocol/Protos/game_packet.proto`, 31-32행

```protobuf
ReqRegister   req_register    = 18;
ResRegister   res_register    = 19;
```

기존 oneof 필드 번호(1~17) 뒤에 순차적으로 추가. 기존 패킷 번호와 충돌 없음. 올바르다.

---

## DB 레이어

### 2.18 [Good] accounts 테이블 스키마

**파일**: `db/schema_game.sql`, 29-36행

```sql
CREATE TABLE IF NOT EXISTS `accounts` (
    `account_id`    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `username`      VARCHAR(64)     NOT NULL,
    `password_hash` VARCHAR(255)    NOT NULL,
    `created_at`    DATETIME        NOT NULL,
    PRIMARY KEY (`account_id`),
    UNIQUE KEY `ux_accounts_username` (`username`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

- `password_hash` VARCHAR(255): BCrypt 해시(60자) 저장에 충분한 크기. Phase 3 전환 시 스키마 변경 불필요.
- `username` UNIQUE KEY: INSERT IGNORE 패턴의 기반. 올바르다.
- `account_id` AUTO_INCREMENT: 순차 ID. 외부 노출 시 계정 수 추정 가능(IDOR 위험). 현재는 ResRegister에서 반환되지만, account_id는 서버 내부용으로만 사용되므로 현 단계에서는 문제없다.

### 2.19 [Good] players 테이블에 account_id FK 추가

**파일**: `db/schema_game.sql`, 20행

```sql
`account_id`  BIGINT UNSIGNED NULL DEFAULT NULL   COMMENT 'accounts.account_id FK',
```

players 테이블에 `account_id` 컬럼이 추가되어 계정-플레이어 관계가 연결된다. NULL 허용으로 기존 데이터(Phase 1 이전)와의 호환성을 유지한다. FOREIGN KEY 제약은 의도적으로 설정하지 않은 것으로 보인다 (성능 우선 설계).

### 2.20 [Good] AccountDbSet Dapper 패턴

**파일**: `GameServer.Database/DbSet/AccountDbSet.cs`

- `InsertAsync`: INSERT IGNORE + `ExecuteAsync` (rows affected 반환). 중복 시 예외 없이 0 반환.
- `SelectByUsernameAsync`: `QuerySingleOrDefaultAsync` + LIMIT 1. 결과 없으면 null.
- 파라미터 바인딩으로 SQL Injection 방지.
- `const string sql`로 쿼리 문자열 상수화. 매 호출마다 문자열 할당 없음.

모두 프로젝트의 기존 DbSet 패턴(PlayerDbSet)과 일관성이 있다. 올바르다.

### 2.21 [Minor] AccountRow 네이밍 컨벤션

**파일**: `GameServer.Database/Rows/AccountRow.cs`

```csharp
public ulong    account_id    { get; set; }
public string   username      { get; set; } = "";
public string   password_hash { get; set; } = "";
public DateTime created_at    { get; set; }
```

프로퍼티 이름이 snake_case로 DB 컬럼명과 일치한다. C# 컨벤션(PascalCase)과 다르지만, Dapper의 자동 매핑(컬럼명 == 프로퍼티명)을 활용하기 위한 의도적 선택이다. PlayerRow도 동일 패턴이므로 프로젝트 내 일관성이 유지된다.

---

## 클라이언트 시나리오

### 2.22 [Good] Register -> Login 플로우

**파일**: `GameClient/Scenarios/BaseRoomScenario.cs`, 20-30행 및 37-53행

```
OnConnected -> ReqRegister 전송
ResRegister 수신 -> SUCCESS 또는 USERNAME_TAKEN -> ReqLogin 전송
ResLogin 수신 -> 로그인 성공 -> 시나리오별 OnLoginSuccessAsync
```

재접속 시 이미 생성된 계정에 대해 Register -> USERNAME_TAKEN -> Login 흐름이 자연스럽게 처리된다. 올바르다.

### 2.23 [Good] 모든 시나리오에서 일관된 Register/Login 처리

BaseRoomScenario, LobbyChatScenario, ReconnectStressScenario 세 시나리오 모두 동일한 Register -> Login 패턴을 사용한다.

- `BaseRoomScenario`: 추상 기반 클래스로 공통 로직 제공 (ResRegister, ResLogin 처리)
- `LobbyChatScenario`: ILoadTestScenario 직접 구현이지만 동일 패턴
- `ReconnectStressScenario`: ILoadTestScenario 직접 구현, ResetForReconnect() 호출 후 동일 패턴

### 2.24 [Minor] LobbyChatScenario의 Register/Login 코드 중복

**파일**: `GameClient/Scenarios/LobbyChatScenario.cs`

LobbyChatScenario는 `ILoadTestScenario`를 직접 구현하며, BaseRoomScenario의 Register/Login 공통 로직을 재사용하지 않는다. Register 응답 처리(39-56행)가 BaseRoomScenario(37-53행)와 거의 동일한 코드다.

**평가**: LobbyChatScenario는 룸 진입 없이 로비 채팅만 하는 시나리오이므로 BaseRoomScenario를 상속하기 어렵다. 향후 Register/Login 공통 로직을 별도 유틸리티 메서드로 추출하면 중복을 줄일 수 있으나, 현재 3개 시나리오 수준에서는 관리 가능하다.
**중요도**: Minor (코드 중복이지만 기능 정확성에 영향 없음).

### 2.25 [Good] ClientContext.ResetForReconnect()

**파일**: `GameClient/Controllers/ClientContext.cs`, 22-30행

```csharp
public void ResetForReconnect()
{
    PlayerId = 0;
    PlayerName = string.Empty;
    RoomEnterSent = false;
    RoomExitScheduled = false;
    RoomEnterRetryCount = 0;
}
```

재접속 시 연결별 상태만 초기화하고, 누적 카운터(`ReconnectCount`, `TotalRoomCycles`)는 유지한다. `Password`도 유지된다. 올바르다.

### 2.26 [Good] 봇 비밀번호 "0000"

**파일**: `GameClient/Controllers/ClientContext.cs`, 14행

```csharp
public string Password { get; set; } = "0000";
```

부하 테스트 봇은 고정 비밀번호 "0000"을 사용한다. 비밀번호 길이 검증(4~16자)을 통과하며, 테스트 용도로 적절하다.

---

## Phase 3 준비도

### 2.27 [Good] BCrypt 전환 영향 범위 분석

Phase 3에서 BCrypt를 도입할 때 변경이 필요한 지점:

| 파일 | 변경 내용 | 난이도 |
|------|-----------|--------|
| `RegisterProcessor.cs` 81행 | `password` -> `BCrypt.HashPassword(password, 11)` | 1줄 변경 |
| `LoginProcessor.cs` 167행 | `account.password_hash != password` -> `!BCrypt.Verify(password, account.password_hash)` | 1줄 변경 |
| DB 스키마 | 변경 불필요 (VARCHAR(255)에 BCrypt 60자 해시 충분) | 없음 |
| AccountRow | 변경 불필요 (password_hash 필드 그대로 사용) | 없음 |
| 클라이언트 | 변경 불필요 (평문 password를 서버에 전송, 해싱은 서버 담당) | 없음 |
| 기존 계정 마이그레이션 | 기존 평문 password를 BCrypt 해시로 일괄 변환 SQL 필요 | 별도 작업 |

**평가**: 전환 포인트가 2곳으로 최소화되어 있다. 주석으로 정확한 위치와 코드가 명시되어 있어 마이그레이션 난이도가 매우 낮다. 스키마와 Row 클래스는 이미 해시 기반으로 설계되어 있어 변경 불필요하다.

### 2.28 [Observation] BCrypt 도입 시 성능 고려

BCrypt는 의도적으로 느린 해시 알고리즘이다. cost factor 11 기준으로 약 100~200ms가 소요된다. 이는 Register와 Login 요청이 I/O 스레드가 아닌 ThreadPool에서 비동기로 실행되므로 서버 처리량에 큰 영향은 없지만, 대규모 동시 로그인 시 ThreadPool 고갈 가능성이 있다.

**권고**: Phase 3에서 BCrypt 도입 시 `Task.Run(() => BCrypt.Verify(...))`로 감싸서 ThreadPool에서 실행하되, 동시 해싱 수를 `SemaphoreSlim`으로 제한하는 것을 고려할 것.

---

## 수정 권고 사항

### Critical

| # | 항목 | 파일 | 설명 |
|---|------|------|------|
| C1 | 평문 비밀번호 저장 | RegisterProcessor.cs:81 | Phase 3 전까지 프로덕션 배포 불가. Phase 3에서 BCrypt 적용 시 해결 예정. |

### Major

없음. 모든 핵심 흐름(인증, 동시성, 에러 처리)이 올바르게 구현되어 있다.

### Minor

| # | 항목 | 파일 | 설명 |
|---|------|------|------|
| M1 | SELECT 재조회 null 방어 | RegisterProcessor.cs:121 | INSERT 성공 후 SELECT 결과가 null인 경우 `created!` NullReferenceException. 방어 코드 추가 권고. |
| M2 | 로그 내 username 원문 | RegisterProcessor.cs:25 | 검증 실패 시 username 원문이 로그에 기록됨. 길이 제한 또는 원문 생략 권고. |
| M3 | proto 필드명 불일치 | login.proto:8 | `player_name` 필드가 실제로는 `username`으로 사용됨. 가독성을 위해 필드명 변경 검토. |
| M4 | LobbyChatScenario 코드 중복 | LobbyChatScenario.cs | Register/Login 처리 로직이 BaseRoomScenario와 중복. 공통 유틸리티 추출 검토. |

---

## 참조 파일 경로

- `E:/MyProject/DhNet_DotNetty/GameServer/Network/RegisterProcessor.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/LoginProcessor.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/SessionComponent.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer/Network/GameServerHandler.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer.Database/DbSet/AccountDbSet.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer.Database/Rows/AccountRow.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer.Database/Rows/PlayerRow.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer.Database/System/GameDbContext.cs`
- `E:/MyProject/DhNet_DotNetty/GameServer.Protocol/Protos/error_codes.proto`
- `E:/MyProject/DhNet_DotNetty/GameServer.Protocol/Protos/register.proto`
- `E:/MyProject/DhNet_DotNetty/GameServer.Protocol/Protos/login.proto`
- `E:/MyProject/DhNet_DotNetty/GameServer.Protocol/Protos/game_packet.proto`
- `E:/MyProject/DhNet_DotNetty/GameClient/Controllers/ClientContext.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Scenarios/BaseRoomScenario.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Scenarios/LobbyChatScenario.cs`
- `E:/MyProject/DhNet_DotNetty/GameClient/Scenarios/ReconnectStressScenario.cs`
- `E:/MyProject/DhNet_DotNetty/db/schema_game.sql`
