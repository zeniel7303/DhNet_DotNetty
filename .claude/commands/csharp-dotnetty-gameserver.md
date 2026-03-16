---
description: C# DotNetty 게임 서버 개발 시 베스트 프랙티스와 패턴을 로드하고 현재 작업에 적용합니다
argument-hint: 작업 중인 컴포넌트 (예: "handler", "session", "lobby", "room", "protobuf", "client")
---

C# DotNetty 게임 서버 개발 가이드라인을 로드합니다. 현재 작업 컨텍스트: $ARGUMENTS

## 즉시 실행 (Immediate Actions)

**지금 바로** Read 도구로 아래 표에서 현재 작업과 관련된 리소스 파일을 읽으세요.
리소스 경로: `.claude/skills/csharp-dotnetty-gameserver/resources/`

| 작업 컴포넌트 | 읽어야 할 리소스 파일 |
|-------------|-------------------|
| handler, pipeline, bootstrap | `architecture.md`, `channel-handlers.md` |
| session, connection | `session-management.md`, `error-handling.md` |
| async, task, await | `async-patterns.md`, `concurrency-safety.md` |
| protobuf, packet, proto | `protobuf-patterns.md` |
| lobby, room, player | `lobby-room-system.md`, `player-system.md` |
| memory, performance | `memory-management.md`, `performance-patterns.md` |
| logging, error | `logging-patterns.md`, `error-handling.md` |

**$ARGUMENTS 가 비어있거나 모호한 경우**: `architecture.md`, `channel-handlers.md`, `async-patterns.md`, `session-management.md` 를 읽으세요.

파일을 읽은 후, 현재 작업에 적용할 핵심 패턴을 1~3줄로 요약하여 출력합니다.

---

## 필수 패턴 체크리스트 (코드 작성 전 반드시 확인)

- [ ] 채널 핸들러: `SimpleChannelInboundHandler<T>` 사용 (ReferenceCount 자동 해제)
- [ ] 비동기 메서드명: `...Async` 접미사
- [ ] 스레드 안전: `ConcurrentDictionary` 사용 — 일반 `Dictionary` + `lock` 조합 금지
- [ ] 블로킹 금지: `.Wait()` / `.Result` 금지 (`Main()` 진입점 제외)
- [ ] 리소스 관리: 리소스 보유 클래스에 `IDisposable` 패턴 적용
- [ ] 파이프라인 표준: `LengthFieldPrepender(2)` + `LengthFieldBasedFrameDecoder` + `ProtobufDecoder(GamePacket.Parser)` + `ProtobufEncoder`
- [ ] 싱글톤: `public static readonly T Instance = new()` 패턴
- [ ] 네이밍: 클래스/메서드 PascalCase, private 필드 `_camelCase`

---

## 코드 완료 후 (Post-Implementation)

코드 작성을 완료한 뒤, 응답 마지막에 반드시:
1. `Skill("dev-docs-update")` — 작업 상태 저장
2. `Task(subagent_type="code-architecture-reviewer")` — 코드 리뷰 실행
