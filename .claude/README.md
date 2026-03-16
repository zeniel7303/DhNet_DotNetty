# Claude Code 자동화 인프라 사용 가이드
# Claude Code Automation Infrastructure Guide

DotNetty 게임 서버 프로젝트를 위한 Claude Code AI 자동화 시스템입니다.

## 🎯 이 시스템은 무엇인가요? (What is this system?)

Claude Code가 **자동으로** 다음을 수행합니다:
- C# 파일 편집 시 DotNetty/ProtoBuf 가이드라인 활성화
- 코드 리뷰, 리팩토링, 문서화 자동 지원
- 작업 컨텍스트 보존 (세션 종료 후에도)

## 🚀 시작하기 (Getting Started)

### 1. Claude Code 앱 실행
- 프로젝트 열기: `D:\Project\DhNet_DotNetty`
- 첫 실행 시 자동으로 npm 의존성 설치됨

### 2. 즉시 사용 가능!
설치는 이미 완료되었습니다. 바로 개발을 시작하세요.

## 📖 기본 사용법 (Basic Usage)

### 자동 스킬 활성화 (Automatic Skill Activation)

**C# 파일 작업 시:**
```
1. GameServer.cs 파일 열기
2. "DotNetty 채널 핸들러 추가해줘" 입력

→ csharp-dotnetty-gameserver 스킬 자동 활성화
→ 채널 핸들러 패턴 자동 적용
→ async/await, IDisposable 패턴 사용
```

**Protocol Buffer 파일 작업 시:**
```
1. game.proto 파일 열기
2. "로그인 메시지 정의 추가해줘" 입력

→ protobuf 패턴 자동 활성화
→ 메시지 정의 베스트 프랙티스 적용
→ 버전 호환성 고려
```

**키워드로 트리거:**
```
"DotNetty 채널 핸들러는 어떻게 만들어?"
→ csharp-dotnetty-gameserver 제안

"Protocol Buffer 직렬화는?"
→ protobuf 패턴 제안
```

## 🤖 에이전트 사용 (Using Agents)

### 코드 리뷰 (Code Review)
```
"GamePacketHandler 코드를 리뷰해줘"

→ code-architecture-reviewer 에이전트 실행
→ dev/active/handler-review/code-review.md 생성
→ 아키텍처 개선점 제안
```

### 리팩토링 계획 (Refactoring Plan)
```
"SessionManager를 더 모듈화하고 싶어"

→ refactor-planner 에이전트 실행
→ 단계별 리팩토링 계획 생성
→ 위험 요소 식별
```

### 문서 생성 (Documentation)
```
"DotNetty 파이프라인 문서 만들어줘"

→ documentation-architect 에이전트 실행
→ API 문서 자동 생성
→ 사용 예시 포함
```

### 계획 검토 (Plan Review)
```
"이 기능 구현 계획을 검토해줘"

→ plan-reviewer 에이전트 실행
→ 계획의 타당성 분석
→ 개선 제안
```

## 📝 Dev Docs 시스템

### 새 작업 시작 (Start New Task)
```
/dev-docs lobby-system

→ dev/active/lobby-system/ 생성
  - lobby-system-plan.md (전략 계획)
  - lobby-system-context.md (핵심 정보)
  - lobby-system-tasks.md (체크리스트)
```

### 컨텍스트 업데이트 (Update Context)
```
/dev-docs-update lobby-system

→ 현재 작업 상태 저장
→ 세션 종료 후에도 작업 이어서 진행 가능
```

## 💡 실전 예제 (Practical Examples)

### 예제 1: 새 기능 추가
```
상황: 로비 시스템에 매칭 기능 추가

1. "로비에 매칭 기능 추가해줘"
   → csharp-dotnetty-gameserver 자동 활성화
   → 스레드 안전한 코드 생성

2. "방금 작성한 코드 리뷰해줘"
   → code-architecture-reviewer 실행
   → 개선점 확인

3. /dev-docs lobby-matching
   → 작업 문서 생성

4. 기능 구현 완료!
```

### 예제 2: 버그 수정
```
상황: 연결 끊김 처리 버그

1. "SessionManager에서 연결 끊김 처리 버그 찾아줘"
   → csharp-dotnetty-gameserver 활성화
   → 채널 생명주기 문제 발견

2. "이 문제 수정해줘"
   → IDisposable 패턴으로 수정
   → 리소스 해제 보장

3. "수정된 코드 테스트해줘"
   → 테스트 케이스 생성
```

### 예제 3: Protocol Buffer 메시지 추가
```
상황: 채팅 메시지 프로토콜 추가

1. chat.proto 파일 열기
   → protobuf 패턴 자동 활성화

2. "채팅 메시지 정의 추가해줘"
   → C2S_Chat, S2C_Chat 메시지 생성
   → 필드 번호 자동 할당
   → 버전 호환성 고려

3. "C# 코드 생성해줘"
   → protoc 명령 실행
   → 생성된 코드 통합
```

## 🎨 스킬별 특징

### csharp-dotnetty-gameserver
**자동 적용되는 패턴:**
- DotNetty 채널 핸들러 구현
- Protocol Buffer 직렬화/역직렬화
- async/await 비동기 패턴
- ConcurrentDictionary 스레드 안전성
- IDisposable 리소스 관리

**리소스 파일:**
- architecture.md - DotNetty 아키텍처
- channel-handlers.md - 채널 핸들러 패턴
- protobuf-patterns.md - Protocol Buffers 사용
- async-patterns.md - 비동기 프로그래밍
- session-management.md - 세션 관리
- lobby-room-system.md - 로비/룸 시스템
- concurrency-safety.md - 동시성 안전성
- error-handling.md - 에러 처리
- memory-management.md - 메모리 관리
- performance-patterns.md - 성능 최적화

## 🔧 커스터마이징 (Customization)

### 스킬 트리거 수정
`skills/skill-rules.json` 파일 편집:

```json
{
  "csharp-dotnetty-gameserver": {
    "promptTriggers": {
      "keywords": ["dotnetty", "channel", ...],  // 키워드 추가
      "intentPatterns": [".*게임.*서버.*"]     // 패턴 추가
    },
    "fileTriggers": {
      "pathPatterns": ["**/*.cs"]  // 경로 수정
    }
  }
}
```

### 새 리소스 추가
스킬 디렉토리에 `.md` 파일 추가:

```
.claude/skills/csharp-dotnetty-gameserver/resources/
└── my-new-guide.md  # 새 가이드 추가
```

메인 `SKILL.md`에 링크 추가:
```markdown
- **[my-new-guide.md](resources/my-new-guide.md)** - 설명
```

## 🐛 문제 해결 (Troubleshooting)

### 스킬이 활성화되지 않을 때
1. **파일 경로 확인**
   - C# 파일이 프로젝트 내에 있는지 확인

2. **skill-rules.json 검증**
   ```bash
   cat .claude/skills/skill-rules.json | jq .
   ```

3. **키워드 사용**
   - "DotNetty", "채널", "핸들러" 등 명시적 키워드 사용

### 에이전트가 실행되지 않을 때
1. **명시적 요청**
   - "리뷰해줘" → "코드를 리뷰해줘"
   - "문서 만들어줘" → "문서를 생성해줘"

2. **에이전트 파일 확인**
   ```bash
   ls .claude/agents/
   ```

### npm 의존성 에러
```bash
cd .claude/hooks
npm install
```

## 🚦 빠른 참조 (Quick Reference)

| 하고 싶은 것 | 명령 |
|------------|------|
| C# 코드 작성 | 파일 열고 요청 → 자동 활성화 |
| 코드 리뷰 | "코드를 리뷰해줘" |
| 리팩토링 계획 | "리팩토링 계획 세워줘" |
| 문서 생성 | "문서를 생성해줘" |
| 작업 시작 | `/dev-docs [작업명]` |
| 작업 저장 | `/dev-docs-update [작업명]` |

---

**프로젝트**: DotNetty Game Server
**기술 스택**: C# .NET, DotNetty, Protocol Buffers
**상태**: ✅ 바로 사용 가능

즐거운 개발 되세요! 🚀
