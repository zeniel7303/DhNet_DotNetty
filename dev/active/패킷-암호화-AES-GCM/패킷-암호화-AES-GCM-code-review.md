# 패킷 암호화 AES-GCM — 아키텍처 코드 리뷰

Last Updated: 2026-03-20

---

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

#### 인바운드 실제 처리 경로 (wire → handler)
```
wire → LengthFieldBasedFrameDecoder → AesGcmDecryptionHandler → ProtobufDecoder → handler
```
올바름. 프레이밍 분리 후 복호화, 복호화 후 protobuf 파싱 순서가 맞다.

#### 아웃바운드 실제 처리 경로 (handler → wire)
DotNetty 아웃바운드는 AddLast 역순으로 처리된다.
```
handler → ProtobufEncoder → AesGcmEncryptionHandler → LengthFieldPrepender → wire
```
올바름. protobuf 직렬화 → 암호화 → length 헤더 부착 순서가 맞다.

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

    var decrypted = AesGcmCryptor.Decrypt(_key, encrypted);  // ← 예외 발생 가능

    var buf = ctx.Allocator.Buffer(decrypted.Length);  // ← Decrypt 성공 시에만 도달
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

#### 서버 AesGcmDecryptionHandler — 적절함
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

#### 서버/클라 AesGcmEncryptionHandler — ExceptionCaught 없음

`AesGcmEncryptionHandler`에는 `ExceptionCaught`가 구현되어 있지 않다.
암호화 중 예외 발생 시 파이프라인 상위(`GameServerHandler`)로 전파된다.
`GameServerHandler`에 `ExceptionCaught`가 있다면 처리되지만, 암호화 레이어에서
발생한 오류임을 구분하는 로그가 없어 디버깅이 어려울 수 있다.

권고: `AesGcmEncryptionHandler`에도 `ExceptionCaught` 추가.
```csharp
public override void ExceptionCaught(IChannelHandlerContext ctx, Exception ex)
{
    GameLogger.Warn("Crypto", $"암호화 실패 ({ctx.Channel.RemoteAddress}): {ex.Message}");
    ctx.CloseAsync();
}
```
중요도: 낮음 (런타임 오류 발생 가능성이 Decrypt보다 훨씬 낮음).

#### 클라 DecryptionHandler — 적절함
서버와 동일 패턴. 로그 + CloseAsync. 올바름.

#### 클라 EncryptionHandler — 서버와 동일 이슈 (ExceptionCaught 없음)

---

## 5. 잠재적 버그 및 엣지 케이스

### 5.1 [버그] LoadTestConfig — Base64 디코딩 예외 미처리

**파일**: `GameClient/Program.cs`, 125-127행 및 202-204행

```csharp
var encKey = string.IsNullOrEmpty(config.EncryptionKey)
    ? null
    : Convert.FromBase64String(config.EncryptionKey);  // ← FormatException 미처리
```

`--encryption-key`에 잘못된 Base64 문자열을 입력하면 `FormatException`이 발생한다.
이 예외는 `ConnectClientAsync`의 외부 `catch (Exception ex)` 블록에서 잡히지만,
연결 오류(`LoadTestStats.IncrementErrors()`)로 집계된다. 실제로는 설정 오류인데
1000개 클라이언트가 모두 "연결 실패"로 보고하는 혼란스러운 상황이 발생한다.

권고: 클라이언트 시작 시점(Program.RunAsync)에서 한 번 검증.
```csharp
if (!string.IsNullOrEmpty(config.EncryptionKey))
{
    try { Convert.FromBase64String(config.EncryptionKey); }
    catch (FormatException)
    {
        GameLogger.Error("Config", "EncryptionKey가 유효한 Base64 형식이 아닙니다.");
        return;
    }
}
```
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
- `ExceptionCaught` → 연결 해제

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
1. `GameClient/Program.cs` — `ConnectClientAsync` 및 `RunReconnectLoopAsync` 두 곳에서
   `Convert.FromBase64String(config.EncryptionKey)` 호출 전에 Base64 유효성 사전 검증 추가.

### 선택적 개선 (낮은 우선순위)
2. `AesGcmEncryptionHandler` (서버/클라) — `ExceptionCaught` 오버라이드 추가.
3. `AesGcmDecryptionHandler.Decode` — `buf` 할당 후 방어적 try/finally 패턴 추가.

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
