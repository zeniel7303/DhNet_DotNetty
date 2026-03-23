# RPG 웹게임 - Tasks

Last Updated: 2026-03-23

> ⚠️ 요구사항 변경: RPG → 뱀파이어 서바이벌 류 (실시간 이동, 4인 코옵)
> 아래 태스크는 업데이트 필요. rpg-webgame-plan.md 참고.

---

## Phase 1: WebSocket 전환
- [ ] 1.1 WebSocketFrameHandler 작성 (BinaryWebSocketFrame ↔ ByteBuf)
- [ ] 1.2 GamePipelineInitializer WebSocket 분기 추가 (/ws 경로)
- [ ] 1.3 WebSocket 연결 테스트 HTML 페이지

## Phase 2: Proto 재정의 + 코드 정리
- [ ] 2.1 lobby.proto, room.proto 삭제
- [ ] 2.2 신규 proto 파일 작성 (뱀서 스타일로 수정 필요)
- [ ] 2.3 LobbyComponent/RoomComponent 삭제
- [ ] 2.4 PlayerComponent 정리

## Phase 3: 게임 서버 컴포넌트
- [ ] 3.1 CharacterComponent (스탯, 레벨, 무기)
- [ ] 3.2 PlayerWorldComponent (실시간 위치, 이동)
- [ ] 3.3 GameSessionComponent (최대 4인 세션)
- [ ] 3.4 EnemyComponent (AI, 이동, 스폰)
- [ ] 3.5 EnemySystem (웨이브 관리, 스폰)
- [ ] 3.6 CombatSystem (자동 공격, 데미지, 경험치)
- [ ] 3.7 PlayerGameController (ReqMove, ReqReady 등)

## Phase 4: DB 확장
- [ ] 4.1 characters 테이블 추가
- [ ] 4.2 CharacterDbSet 작성

## Phase 5: HTML5 클라이언트
- [ ] 5.1 GameClient.Web 프로젝트 생성
- [ ] 5.2 protobuf.js 통합
- [ ] 5.3 로그인 UI + WebSocket 연결
- [ ] 5.4 게임 루프 + Canvas 렌더링
- [ ] 5.5 실시간 이동 입력 처리
- [ ] 5.6 적 렌더링 + 전투 이펙트
- [ ] 5.7 레벨업 선택 UI
- [ ] 5.8 4인 코옵 UI

## Phase 6: 테스트
- [ ] 6.1 VampireSurvivorScenario 부하 테스트
- [ ] 6.2 4인 동시 접속 통합 테스트
