# DhNet DotNetty GameServer - Claude Code 가이드

## 프로젝트 개요

C++ 기반 게임 서버(DhNet)를 C# .NET 9 + DotNetty로 포팅한 멀티플레이어 게임 서버.
로비/룸 게임 로직, MySQL DB 레이어, REST API 웹 서버, 부하 테스트 인프라까지 갖춘 완성형 서버.

**기술 스택**: C# .NET 9, DotNetty 0.7.6, Protocol Buffers, MySQL, Dapper, ASP.NET Core
**직렬화**: Protocol Buffers (LengthFieldBasedFrameDecoder + ProtobufDecoder/Encoder)

## 프로젝트 구조

```
DhNet_DotNetty/
├── GameServer/          # 서버 코어 (Pipeline, Handler, Session, System 레이어)
│   └── Web/             # ASP.NET Core 기반 관리용 REST API
├── GameClient/          # 테스트 클라이언트 + 부하 테스트 시나리오
├── GameServer.Protocol/ # Protocol Buffers .proto 정의
├── GameServer.Database/ # MySQL + Dapper DB 레이어 (DbSet 패턴)
├── Common/              # 공통 설정 (appsettings.json 기반 외부화)
├── Legacy/              # 초기 에코서버/직렬화 실험 코드
├── db/                  # DB 스키마 SQL
├── dev/                 # 개발 문서 (작업별 plan/context/tasks)
└── .claude/             # Claude Code 자동화 인프라
```

## .claude/ 인프라 시스템

**항상 이 시스템을 활용하여 작업한다.**

### 스킬 시스템 (자동 활성화)
`.claude/skills/csharp-dotnetty-gameserver/` 에 정의된 패턴을 따른다:
- C# 파일, .proto 파일 작업 시 자동 활성화
- DotNetty 채널 핸들러, async/await, ConcurrentDictionary, IDisposable 패턴 적용
- 상세 패턴은 `.claude/skills/csharp-dotnetty-gameserver/resources/` 참조

### 에이전트 (Task 도구로 실행)
- **code-architecture-reviewer** - 코드 리뷰 요청 시 사용
- **refactor-planner** - 리팩토링 계획 수립 시 사용
- **documentation-architect** - 문서 생성 시 사용
- **plan-reviewer** - 구현 계획 검토 시 사용

### 커스텀 명령
- `/dev-docs [작업명]` - 새 작업 시작 시 `dev/active/[작업명]/` 구조 생성
- `/dev-docs-update [작업명]` - 현재 작업 상태 저장

## 코딩 컨벤션

### 필수 패턴
```csharp
// 1. 채널 핸들러: SimpleChannelInboundHandler 사용
public class MyHandler : SimpleChannelInboundHandler<GamePacket> { }

// 2. 비동기 우선
public async Task<T> GetAsync(...) { ... }

// 3. 스레드 안전 컬렉션
private readonly ConcurrentDictionary<int, Session> _sessions = new();

// 4. 리소스 관리
public class GameSession : IDisposable { ... }
```

### 금지 사항
- 동기 블로킹 I/O (`.Wait()`, `.Result`) - 단, `Main()` 진입점 제외
- 일반 `Dictionary` + `lock` 조합 (성능 저하)
- 단일 핸들러에 여러 책임 부여

### 네이밍
- 클래스/메서드: PascalCase
- 로컬 변수: camelCase
- private 필드: `_camelCase`
- 비동기 메서드: `...Async` 접미사

## 직렬화 전략

| 상황 | 권장 방식 |
|------|-----------|
| 게임 서버 (크로스 플랫폼) | Protocol Buffers |
| C#/Unity 전용 | MemoryPack |
| 범용 크로스 플랫폼 | MessagePack |

**게임 서버 파이프라인 표준 구성:**
```csharp
pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(MessageType.Parser));
pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
pipeline.AddLast("handler", new GameServerHandler());
```

## 작업 워크플로우 (자동 실행 규칙 - 사용자가 요청하지 않아도 반드시 실행)

### RULE 0: C# 코드 작성 전 → csharp-dotnetty-gameserver 스킬 실행 [필수]
.cs 또는 .proto 파일을 작성/수정하기 **직전에** 반드시 Skill 도구로 `csharp-dotnetty-gameserver`를 실행한다.
- 트리거: C#/proto 파일 작업 시작 시점
- 실행: `Skill("csharp-dotnetty-gameserver", "[작업 컴포넌트]")`
- 스킬이 지시하는 리소스 파일을 Read 도구로 읽고 패턴을 확인한 뒤 코드를 작성한다.
- **절대로 건너뛰지 않는다. 먼저 스킬을 실행하고, 그 다음 코드를 작성한다.**

### RULE 1: 새 작업 시작 시 → dev-docs 자동 실행 [필수]
사용자가 새로운 기능/작업 구현을 요청하면 **코드 작성 전에** 반드시 Skill 도구로 `dev-docs`를 실행한다.
- 트리거: "~구현해줘", "~만들어줘", "~추가해줘", "새 기능", "시작하자" 등
- 실행: `Skill("dev-docs", "[작업명]")`
- **절대로 건너뛰지 않는다. 사용자가 언급하지 않아도 자동 실행.**

### RULE 2: 코드 작업 완료 후 → dev-docs-update + code-architecture-reviewer 자동 실행 [필수]
C# 파일(.cs) 또는 .proto 파일을 하나 이상 작성/수정하고 나면 **응답 마지막에** 반드시 두 가지를 순서대로 실행한다.
1. `Skill("dev-docs-update")` — 작업 상태 저장
2. `Task(subagent_type="code-architecture-reviewer", prompt="방금 작성한 코드를 리뷰해줘")` — 코드 리뷰
- **둘 다 반드시 실행. 어느 하나도 생략 불가. 사용자가 요청하지 않아도 자동 실행.**

### 순서
1. 새 기능 요청 → **즉시 `dev-docs` 실행** → 코드 작성 준비
2. C# 코드 작성 직전 → **`csharp-dotnetty-gameserver` 스킬 실행** → 리소스 로드 및 패턴 확인
3. 코드 작성
4. 코드 완성 → **즉시 `dev-docs-update` 실행** → **즉시 `code-architecture-reviewer` 에이전트 실행**

## 참고 문서

- `.claude/README.md` - 인프라 전체 사용법
- `.claude/SERIALIZATION_NOTES.md` - 직렬화 방식 비교 및 결정 기록
- `.claude/skills/csharp-dotnetty-gameserver/SKILL.md` - 스킬 상세 가이드
