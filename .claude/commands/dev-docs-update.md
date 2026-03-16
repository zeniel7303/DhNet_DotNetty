---
description: 컨텍스트 압축 전에 개발 문서를 업데이트합니다 | Update dev documentation before context compaction
argument-hint: 선택사항 - 집중할 특정 컨텍스트 또는 작업 (포괄적 업데이트를 위해 비워둠) | Optional - specific context or tasks to focus on (leave empty for comprehensive update)
---

We're approaching context limits. Please update the development documentation to ensure seamless continuation after context reset.

컨텍스트 한계에 접근하고 있습니다. 컨텍스트 리셋 후에도 원활한 연속성을 보장하기 위해 개발 문서를 업데이트해 주세요.

## 필수 업데이트 (Required Updates)

### 1. 활성 작업 문서 업데이트 (Update Active Task Documentation)
`/dev/active/`의 각 작업에 대해:
- `[task-name]-context.md` 업데이트:
  - 현재 구현 상태
  - 이 세션에서 내린 주요 결정사항
  - 수정된 파일 및 이유
  - 발견된 차단 요소 또는 문제
  - 다음 즉시 단계
  - Last Updated 타임스탬프

- `[task-name]-tasks.md` 업데이트:
  - 완료된 작업을 ✅로 표시
  - 발견된 새 작업 추가
  - 진행 중인 작업을 현재 상태로 업데이트
  - 필요시 우선순위 재정렬

### 2. 세션 컨텍스트 캡처 (Capture Session Context)
다음에 대한 관련 정보 포함:
- 해결된 복잡한 문제
- 내린 아키텍처 결정사항
- 발견하고 수정한 까다로운 버그
- 발견된 통합 지점
- 사용된 테스팅 접근법
- 수행된 성능 최적화

### 3. 메모리 업데이트 (Update Memory - if applicable)
- 프로젝트 메모리/문서에 새로운 패턴 또는 솔루션 저장
- 발견된 엔티티 관계 업데이트
- 시스템 동작에 대한 관찰 추가

### 4. 미완성 작업 문서화 (Document Unfinished Work)
- 컨텍스트 한계에 접근했을 때 작업 중이던 내용
- 부분적으로 완료된 기능의 정확한 상태
- 재시작 시 실행해야 할 명령
- 영구 수정이 필요한 임시 해결책

### 5. 인수인계 노트 생성 (Create Handoff Notes)
새 대화로 전환하는 경우:
- 편집 중인 정확한 파일 및 라인
- 현재 변경사항의 목표
- 주의가 필요한 커밋되지 않은 변경사항
- 작업을 검증하기 위한 테스트 명령

## 추가 컨텍스트 (Additional Context): $ARGUMENTS

**우선순위 (Priority)**: 코드만으로는 재발견하거나 재구성하기 어려운 정보를 캡처하는 데 집중합니다.

---

## 필수 후속 작업 (Required Follow-up)

문서 업데이트 완료 후 **반드시** 다음을 실행합니다:

### 코드 아키텍처 리뷰
이 세션에서 .cs 또는 .proto 파일을 작성/수정했다면, Task 도구로 code-architecture-reviewer 에이전트를 실행합니다:

```
Task(
  subagent_type="code-architecture-reviewer",
  prompt="이 세션에서 작성/수정된 코드를 리뷰해줘. 활성 작업 문서는 dev/active/ 에 있음."
)
```

리뷰 결과는 `dev/active/[task-name]/[task-name]-code-review.md` 에 자동 저장됩니다.
리뷰 완료 후 주요 발견사항을 확인하고, 필요한 수정이 있으면 사용자에게 보고합니다.
