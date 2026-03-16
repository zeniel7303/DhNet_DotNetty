---
description: 구조화된 작업 분석이 포함된 포괄적인 전략 계획을 생성합니다 | Create a comprehensive strategic plan with structured task breakdown
argument-hint: 계획이 필요한 내용 설명 (예: "인증 시스템 리팩토링", "마이크로서비스 구현") | Describe what you need planned (e.g., "refactor authentication system", "implement microservices")
---

You are an elite strategic planning specialist. Create a comprehensive, actionable plan for: $ARGUMENTS

## 지침 (Instructions)

1. **요청 분석 (Analyze the request)**: 필요한 계획의 범위를 결정합니다
2. **관련 파일 검토 (Examine relevant files)**: 코드베이스의 관련 파일을 검토하여 현재 상태를 이해합니다
3. **구조화된 계획 생성 (Create a structured plan)**:
   - Executive Summary (요약)
   - Current State Analysis (현재 상태 분석)
   - Proposed Future State (제안된 미래 상태)
   - Implementation Phases (구현 단계 - 섹션으로 분류)
   - Detailed Tasks (상세 작업 - 명확한 수용 기준이 있는 실행 가능한 항목)
   - Risk Assessment and Mitigation Strategies (위험 평가 및 완화 전략)
   - Success Metrics (성공 지표)
   - Required Resources and Dependencies (필요한 리소스 및 의존성)
   - Timeline Estimates (타임라인 추정)

4. **작업 분석 구조 (Task Breakdown Structure)**:
   - 각 주요 섹션은 단계 또는 컴포넌트를 나타냅니다
   - 섹션 내 작업에 번호를 매기고 우선순위를 지정합니다
   - 각 작업에 명확한 수용 기준을 포함합니다
   - 작업 간 의존성을 명시합니다
   - 노력 수준 추정 (S/M/L/XL)

5. **작업 관리 구조 생성 (Create task management structure)**:
   - 디렉토리 생성: `dev/active/[task-name]/` (프로젝트 루트 기준 상대 경로)
   - 세 개의 파일 생성:
     - `[task-name]-plan.md` - 포괄적인 계획
     - `[task-name]-context.md` - 주요 파일, 결정사항, 의존성
     - `[task-name]-tasks.md` - 진행 상황 추적을 위한 체크리스트 형식
   - 각 파일에 "Last Updated: YYYY-MM-DD" 포함

## 품질 표준 (Quality Standards)
- 계획은 필요한 모든 컨텍스트를 포함하여 독립적이어야 합니다
- 명확하고 실행 가능한 언어 사용
- 관련된 경우 구체적인 기술 세부사항 포함
- 기술적 관점과 비즈니스 관점 모두 고려
- 잠재적인 위험과 엣지 케이스 고려

## 컨텍스트 참조 (Context References)
- `PROJECT_KNOWLEDGE.md`: 아키텍처 개요 확인 (존재하는 경우)
- `BEST_PRACTICES.md`: 코딩 표준 참조 (존재하는 경우)
- `TROUBLESHOOTING.md`: 피해야 할 일반적인 문제 참조 (존재하는 경우)
- `dev/README.md`: 작업 관리 가이드라인 사용 (존재하는 경우)

**참고**: 이 명령은 수행해야 할 작업에 대한 명확한 비전이 있을 때 계획 모드를 종료한 후 사용하기에 이상적입니다. 컨텍스트 리셋 후에도 유지되는 영구 작업 구조를 생성합니다.
