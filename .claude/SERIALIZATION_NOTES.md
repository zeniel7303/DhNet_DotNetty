# DotNetty 직렬화 가이드

## 프로젝트 구조

### MemoryPack.DotNetty / MessagePack.DotNetty 프로젝트
- **용도**: MemoryPack/MessagePack을 DotNetty에서 사용하기 위한 커스텀 인코더/디코더 래퍼
- **생성 이유**: DotNetty에 공식 MemoryPack/MessagePack 인코더가 없어 직접 구현
- **현재 상태**: 에코 서버에서는 미사용, 향후 재사용을 위해 유지
- **재사용 방법**: 다른 프로젝트에서 ProjectReference로 참조 가능

**구현 내용:**
```csharp
// MemoryPackEncoder: object → MemoryPackSerializer → IByteBuffer
// MemoryPackDecoder: IByteBuffer → MemoryPackSerializer → object
// MessagePackEncoder: object → MessagePackSerializer → IByteBuffer
// MessagePackDecoder: IByteBuffer → MessagePackSerializer → object
```

## 현재 에코 서버 구성
- **직렬화**: 없음 (원시 IByteBuffer 사용)
- **프레이밍**: LengthFieldPrepender + LengthFieldBasedFrameDecoder
- **용도**: 간단한 에코 테스트용
- **참고**: MemoryPack/MessagePack 프로젝트는 존재하지만 파이프라인에서 제거됨

## 게임 서버 프로젝트 생성 시 권장 사항

### Protocol Buffers 사용 (권장)
```csharp
// 서버 파이프라인
pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(MessageType.Parser));
pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
pipeline.AddLast("handler", new GameServerHandler());
```

**장점:**
- 크로스 플랫폼 (C#, C++, Java, Python, Go 등)
- 강타입 메시지
- 스키마 진화 지원 (버전 관리 용이)
- 성능 우수
- 산업 표준

**NuGet 패키지:**
- `Google.Protobuf`
- `DotNetty.Codecs.Protobuf` (또는 커스텀 인코더 구현)

### 대안 직렬화 옵션

#### MessagePack
- 크로스 플랫폼
- Protocol Buffers보다 빠름
- 작은 페이로드
- 다형성 지원

#### MemoryPack
- 극도로 빠름 (C# 전용)
- C#/Unity 클라이언트 전용 게임에 적합
- 다형성 제한적
- 버전 관리 어려움

## 제거된 구성 (참고용)

### 이전에 있던 MemoryPack/MessagePack
```csharp
// 제거됨 - 핸들러가 IByteBuffer를 처리하므로 타입 불일치 발생
pipeline.AddLast("memorypack-dec", new MemoryPackDecoder());
pipeline.AddLast("memorypack-enc", new MemoryPackEncoder());
```

**문제점:**
- 인코더/디코더가 object를 출력하지만 핸들러는 IByteBuffer를 기대
- 타입 불일치로 통신 에러 발생

## 게임 서버 구현 체크리스트

- [ ] Protocol Buffers .proto 파일 정의
- [ ] protoc 컴파일러로 C# 코드 생성
- [ ] 서버/클라이언트 파이프라인에 Protobuf 인코더/디코더 추가
- [ ] 핸들러에서 강타입 메시지 처리
- [ ] 메시지 버전 관리 전략 수립
