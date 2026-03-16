# Protocol Buffers 사용 패턴
# Protocol Buffers Usage Patterns

## 메시지 정의 (Message Definition)

### 기본 구조
```protobuf
syntax = "proto3";

package game.protocol;

// 패킷 헤더
message PacketHeader {
  uint32 packet_id = 1;
  uint32 sequence = 2;
}

// 로그인 요청
message C2S_Login {
  string username = 1;
  string password = 2;
}

// 로그인 응답
message S2C_Login {
  bool success = 1;
  string message = 2;
  int32 player_id = 3;
}
```

## C# 코드 생성
```bash
protoc --csharp_out=. *.proto
```

## 직렬화/역직렬화 (Serialization/Deserialization)

### 직렬화
```csharp
public IByteBuffer SerializePacket<T>(T message) where T : IMessage
{
    using var stream = new MemoryStream();
    message.WriteTo(stream);
    var bytes = stream.ToArray();

    // 길이 + 데이터
    var buffer = Unpooled.Buffer(2 + bytes.Length);
    buffer.WriteShort(bytes.Length);
    buffer.WriteBytes(bytes);

    return buffer;
}
```

### 역직렬화
```csharp
public T DeserializePacket<T>(IByteBuffer buffer) where T : IMessage<T>, new()
{
    var parser = new MessageParser<T>(() => new T());
    var bytes = new byte[buffer.ReadableBytes];
    buffer.ReadBytes(bytes);

    return parser.ParseFrom(bytes);
}
```

## 버전 관리 (Versioning)

### ✅ 호환성 유지 방법
```protobuf
message PlayerInfo {
  string name = 1;
  int32 level = 2;
  // 새 필드 추가 (이전 버전과 호환)
  int32 experience = 3;  // 새로 추가
}
```

### ❌ 호환성 깨는 방법
```protobuf
message PlayerInfo {
  string name = 1;
  // int32 level = 2;  // 필드 번호 변경 또는 삭제 (호환성 깨짐!)
  float level_float = 2;  // 타입 변경 (위험!)
}
```

## 베스트 프랙티스

1. **필드 번호는 절대 변경하지 않습니다**
2. **optional, repeated 사용으로 확장성 확보**
3. **enum은 0부터 시작 (기본값)**
4. **메시지 이름은 명확하게 (C2S_, S2C_ 접두사)**
