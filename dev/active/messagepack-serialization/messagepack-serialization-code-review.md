# MessagePack 직렬화 교체 코드 리뷰

**브랜치**: `feature/messagepack-serialization`
**커밋**: `45c237a feat: Protobuf → MessagePack 직렬화 교체 및 ISerializer 추상화`
**리뷰 일자**: 2026-03-23

---

## 종합 평가

전반적으로 설계가 명확하고 일관성이 높다. ISerializer 추상화, Union 어노테이션 정확성, DotNetty 코덱 패턴 모두 올바르게 구현되어 있다. 발견된 버그는 없으며, 아래 항목들은 개선 권고 수준이다.

---

## 1. ISerializer 추상화 설계 품질

**평가: 양호**

```csharp
public interface ISerializer
{
    byte[] Serialize(GamePacket packet);
    GamePacket? Deserialize(byte[] data);  // 실패 시 null 반환
}
```

- `GamePacket`에 타입을 직접 바인딩하고 있어 범용 제네릭 직렬화기(`ISerializer<T>`)는 아니지만, 이 서버에서 직렬화 대상이 `GamePacket` 단일 타입이므로 현재 스코프에는 적절하다.
- `MessagePackGameSerializer.Instance` 싱글톤 패턴과 DI(생성자 주입) 병행 제공은 좋은 구성이다. `GamePipelineInitializer`에서 `serializer ?? MessagePackGameSerializer.Instance`로 기본값을 처리하여 하위 호환을 유지하고 있다.

**개선 권고:**

`Serialize`의 반환 타입이 `byte[]`이다. 대용량 패킷이나 고빈도 전송 환경에서는 매번 힙 배열을 할당한다. 향후 성능 최적화가 필요해지면 `ReadOnlyMemory<byte>` 또는 `IBufferWriter<byte>` 오버로드를 추가하는 것을 고려할 수 있다. 현재 규모에서는 문제없다.

---

## 2. MessagePack Union / Key 어노테이션 정확성

**평가: 정확**

`IPacketPayload`의 `[Union]` 태그와 `PacketType` enum 값이 1:1로 대응하며, `GamePacket.TypeMap`과도 완전히 일치한다. 세 곳(enum, Union 어노테이션, TypeMap)이 동기화되어 있는지 수동으로 대조한 결과, 19개 타입 모두 일치 확인.

**잠재적 위험 — 동기화 누락:**

패킷 타입을 추가할 때 반드시 세 곳을 동시에 수정해야 한다.

1. `PacketType` enum 값 추가
2. `IPacketPayload`의 `[Union]` 어노테이션 추가
3. `GamePacket.TypeMap` 딕셔너리 항목 추가

이 중 하나라도 빠지면 런타임에 `KeyNotFoundException`(TypeMap 누락) 또는 역직렬화 silently null(Union 누락)이 발생한다. 컴파일 타임에 잡히지 않는다.

**권고:** 주석이나 별도 문서에 "패킷 추가 체크리스트 3곳"을 명시해두면 향후 실수를 방지할 수 있다.

---

## 3. DotNetty 코덱 구현 패턴

**평가: 올바름**

### GamePacketDecoder

```csharp
public sealed class GamePacketDecoder : MessageToMessageDecoder<IByteBuffer>
```

- `MessageToMessageDecoder<IByteBuffer>` 사용은 `LengthFieldBasedFrameDecoder` 뒤에 배치할 때의 올바른 패턴이다.
- `message.ReadBytes(bytes)`로 버퍼를 소비하고 있어 참조 누수 없음.
- 역직렬화 실패 시 `output`에 아무것도 추가하지 않아 파이프라인 하단 핸들러로 전파되지 않는다. 올바른 처리 방식.

**개선 권고:**

`new byte[message.ReadableBytes]` 배열 할당이 패킷마다 발생한다. DotNetty `IByteBuffer`는 `GetIoBuffer()` / `ToArray()` 대신 `ArraySegment`를 직접 얻을 수 있는 경우가 있으나, MessagePack 2.5의 `Deserialize<T>(ReadOnlyMemory<byte>)` 오버로드를 활용하면 중간 배열을 줄일 수 있다. 현재 구현은 정확성 면에서 문제없다.

### GamePacketEncoder

```csharp
public sealed class GamePacketEncoder : MessageToByteEncoder<GamePacket>
```

- `MessageToByteEncoder<GamePacket>` 사용 및 `output.WriteBytes(bytes)` 패턴은 정확하다.
- `LengthFieldPrepender` 앞에 배치하는 주석 방향도 맞다.

---

## 4. GamePacket.From<T>() / As<T>() API 안전성

**평가: 양호, 단 한 가지 엣지 케이스 존재**

### From<T>()

```csharp
public static GamePacket From<T>(T payload) where T : IPacketPayload
    => new() { Type = TypeMap[typeof(T)], Payload = payload };
```

`TypeMap`에 없는 타입으로 호출하면 `KeyNotFoundException`이 발생한다. 그러나 제네릭 제약 `where T : IPacketPayload`가 있고 TypeMap이 모든 구현 타입을 포함하므로, 외부에서 임의로 `IPacketPayload`를 구현하지 않는 한 문제없다. 내부 코드베이스에서는 안전하다.

### As<T>()

```csharp
public T As<T>() where T : class, IPacketPayload
    => (T)(Payload ?? throw new InvalidOperationException(...));
```

- `Payload`가 null이면 `InvalidOperationException`을 던진다. 적절한 실패 처리.
- `Payload`가 null이 아니지만 타입이 다를 경우 (`InvalidCastException`) — 이것은 `(T)` 직접 캐스트이므로 `InvalidCastException`이 발생한다. 호출자가 이를 인지하고 있어야 한다.

**주목할 패턴:** `GameServerHandler`에서 `packet.As<ReqRegister>()`, `packet.As<ReqLogin>()`을 `switch (packet.Type)`로 타입을 먼저 검증한 후 호출하므로, 현재 코드 흐름에서는 타입 불일치가 발생하지 않는다. 안전한 사용 패턴.

**개선 권고 (선택적):** `As<T>()` 대신 `TryAs<T>(out T? result)` 패턴을 제공하면 방어적 코드 작성이 가능하지만, 현재 switch 기반 디스패치 구조에서는 과잉 설계일 수 있다.

---

## 5. 역직렬화 실패(null 반환) 처리 흐름

**평가: 흐름은 올바르나 가시성 부족**

```
클라이언트 → [네트워크] → LengthFieldBasedFrameDecoder
                           → GamePacketDecoder (역직렬화 실패 시 null → output에 미추가)
                           → GameServerHandler.ChannelRead0 (도달 안 함)
```

역직렬화가 실패하면 패킷은 파이프라인에서 조용히 소멸한다. 공격자가 의도적으로 malformed 데이터를 전송해도 연결이 유지된다.

**현재 동작의 장단점:**
- 장점: 단순한 패킷 오류로 인해 세션이 끊기지 않는다.
- 단점: 악의적 클라이언트가 계속 잘못된 패킷을 보내더라도 차단되지 않는다. `PacketRatePolicy`가 역직렬화 실패 패킷을 카운트하지 않으므로 rate limiting도 작동하지 않는다.

**권고:** `GamePacketDecoder`에서 역직렬화 실패 시 경고 로그를 남기는 것을 고려하라. 최소한 디버깅 시 가시성이 높아진다.

```csharp
// 권고 예시
var packet = _serializer.Deserialize(bytes);
if (packet == null)
{
    GameLogger.Warn("GamePacketDecoder", $"역직렬화 실패 — 채널: {context.Channel.RemoteAddress}");
    return;
}
output.Add(packet);
```

---

## 6. MessagePackSecurity.UntrustedData 적용의 적절성

**평가: 올바름, 필수적**

```csharp
private static readonly MessagePackSerializerOptions Options =
    MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
```

게임 서버는 외부 클라이언트로부터 데이터를 수신하므로 `UntrustedData` 보안 모드는 필수다. 이 설정은 다음을 제한한다:
- 최대 배열/맵 크기 제한 (DoS 방어)
- 타입 필터링 강화

`Standard`(기본값)를 사용했을 경우 악의적 클라이언트가 거대한 중첩 컬렉션으로 역직렬화 폭탄을 시도할 수 있다. `UntrustedData` 적용은 올바른 판단이다.

---

## 7. 전체 패턴 일관성 — GamePacket.From 사용 누락 검사

**평가: 일관성 있음, 누락 없음**

서버 → 클라이언트 방향의 패킷 전송을 전수 확인:

| 위치 | 전송 패킷 | GamePacket.From 사용 |
|------|-----------|----------------------|
| `LoginProcessor` | `ResLogin` (다수) | 모두 사용 |
| `RegisterProcessor` | `ResRegister` (다수) | 모두 사용 |
| `PlayerHeartbeatController` | `ResHeartbeat` | 사용 |
| `LobbyComponent` (미확인) | `ResLobbyList`, `NotiLobbyChat` 등 | 별도 확인 필요 |
| `RoomComponent` (미확인) | `NotiRoomEnter`, `NotiRoomChat` 등 | 별도 확인 필요 |

`LoginProcessor`와 `RegisterProcessor`는 모두 `GamePacket.From(new Res*{...})` 패턴을 일관되게 사용하고 있다. `PlayerHeartbeatController`도 `GamePacket.From(new ResHeartbeat())` 패턴 사용. 리뷰 범위 내의 파일에서는 직접 `new GamePacket { ... }` 생성자 호출이 없다.

**주의:** `LobbyComponent`, `RoomComponent`, `PlayerLobbyComponent`, `PlayerRoomComponent`는 이번 리뷰 범위에 포함되지 않았으나, 이들 파일에서도 `GamePacket.From<T>()` 패턴이 일관되게 사용되는지 확인을 권고한다.

---

## 8. 잠재적 버그 및 개선 포인트

### [P1] ErrorCode enum — MessagePack Key 어노테이션 누락

```csharp
// ErrorCode.cs
public enum ErrorCode
{
    Success = 0,
    ServerFull = 1000,
    ...
}
```

`ErrorCode`는 `[MessagePackObject]` 없이 일반 enum으로 선언되어 있다. MessagePack은 enum을 기본적으로 정수로 직렬화하므로 동작에는 문제가 없다. 그러나 문서화 차원에서 의도가 명확하다.

**현재 동작**: `int` 형태로 직렬화. `ErrorCode.Success = 0`, `ServerFull = 1000` 등 정수 값이 그대로 전송됨. 정상 동작.

### [P2] 빈 페이로드 MessagePackObject — 직렬화 결과

```csharp
[MessagePackObject]
public sealed class ReqRoomEnter : IPacketPayload { }

[MessagePackObject]
public sealed class ReqRoomExit : IPacketPayload { }

[MessagePackObject]
public sealed class ReqHeartbeat : IPacketPayload { }

[MessagePackObject]
public sealed class ResHeartbeat : IPacketPayload { }

[MessagePackObject]
public sealed class ResRoomExit : IPacketPayload { }
```

필드가 없는 `[MessagePackObject]` 클래스는 MessagePack에서 빈 맵/배열(`{}`)으로 직렬화된다. 기능적으로 올바르지만, 클라이언트 구현 시 이를 인지해야 한다. 빈 구조체는 `[MessagePackObject(keyAsPropertyName: false)]` 기본값이므로 빈 배열 `[]`로 직렬화된다.

### [P3] PacketPairPolicy의 ConcurrentQueue.Count() 성능

```csharp
// SessionComponent.cs
_packetPolicies =
[
    PacketPairPolicy.Create(t => _packetQueue.Count(p => p.Type == t)),
    PacketRatePolicy.Create(),
];
```

`_packetQueue.Count(p => p.Type == t)`는 `ConcurrentQueue<T>`의 전체 LINQ 순회다. 패킷 타입당 O(n) 스캔이 수신 패킷마다 실행된다. 현재 큐 크기가 작고 `DefaultMaxCount = 1`로 제한되어 있어 실제로는 큐에 원소가 거의 없으므로 성능 영향은 미미하다.

그러나 이 코드는 `GameServerHandler.ChannelRead0` → `ProcessPacket` → `PacketPairPolicy.VerifyPolicy` 경로에서 **I/O 이벤트 루프 스레드**에서 실행된다. 설계적으로 이 경로는 최대한 빠르게 반환해야 한다.

**권고:** 향후 채팅 패킷 예외 처리(`ExclusionMaxCount`)가 활성화되면 타입별 카운터를 `ConcurrentDictionary<PacketType, int>`로 관리하는 것을 고려하라.

### [P4] `ResLogin.PlayerId` 필드명 불일치

```csharp
// Login.cs
public sealed class ResLogin : IPacketPayload
{
    [Key(0)] public ulong    PlayerId   { get; set; }
    [Key(1)] public string   PlayerName { get; set; } = string.Empty;
    [Key(2)] public ErrorCode ErrorCode { get; set; }
}
```

`ReqLogin`의 응답인 `ResLogin`에는 `PlayerId`가 있으나, 실제 `LoginProcessor`에서는 `player.AccountId`를 이 필드에 담아 전송한다:

```csharp
await session.SendAsync(GamePacket.From(new ResLogin
{
    PlayerId = player.AccountId, PlayerName = player.Name, ...
}));
```

`player_id`와 `account_id`는 동일한 값을 가리키고 있으나(리팩토링 이력에서 `player_id → account_id`로 변경), 프로토콜 레이어의 `ResLogin.PlayerId`는 아직 구 이름을 사용하고 있다. 클라이언트와의 프로토콜 문서에서 혼동이 생길 수 있다.

**권고:** `ResLogin.PlayerId`를 `AccountId`로 통일하거나, 필드명이 의도적으로 클라이언트에 노출되는 "플레이어 ID"임을 주석으로 명시하라.

### [P5] `GamePacketEncoder` — Serialize 예외 미처리

```csharp
protected override void Encode(IChannelHandlerContext context, GamePacket message, IByteBuffer output)
{
    var bytes = _serializer.Serialize(message);
    output.WriteBytes(bytes);
}
```

`ISerializer.Serialize`가 예외를 던지면 DotNetty 파이프라인의 `ExceptionCaught`로 전파된다. `GameServerHandler.ExceptionCaught`에서 연결을 닫으므로 최종적으로는 처리된다. 그러나 `Decode`에서는 try-catch로 null을 반환하는 반면 `Encode`에서는 예외를 그대로 전파하는 비대칭 구조다.

서버 측에서 생성하는 패킷은 신뢰할 수 있으므로 직렬화 실패는 프로그래밍 오류이고, 예외 전파가 더 적절하다. 현재 구현이 의도적이라면 주석으로 명시하는 것이 좋다.

---

## 요약 테이블

| 항목 | 평가 | 우선순위 |
|------|------|---------|
| ISerializer 추상화 설계 | 양호 | — |
| Union/Key 어노테이션 정확성 | 정확 | — |
| DotNetty 코덱 패턴 | 올바름 | — |
| GamePacket.From/As API 안전성 | 양호 | — |
| 역직렬화 실패 처리 흐름 | 동작은 올바르나 가시성 부족 | P2 |
| UntrustedData 적용 | 필수 적용, 올바름 | — |
| GamePacket.From 사용 일관성 | 리뷰 범위 내 누락 없음 | — |
| ErrorCode enum 어노테이션 | 기능 문제 없음 | P3 |
| 빈 페이로드 직렬화 | 기능 문제 없음, 클라이언트 인지 필요 | P3 |
| PacketPairPolicy LINQ Count | 현재 무해, 확장 시 주의 | P3 |
| ResLogin.PlayerId 필드명 | account_id와 혼동 가능 | P2 |
| Encode 예외 비대칭 | 의도적이라면 주석 명시 권고 | P3 |
| 패킷 추가 체크리스트 3곳 | 프로세스 문서화 필요 | P2 |

**P1**: 버그 (없음)
**P2**: 주의 권고 (기능 영향 없으나 개선 권장)
**P3**: 향후 고려 (현재 무해)
