# RPG 웹게임 - Tasks

Last Updated: 2026-03-24

---

## Phase 1: WebSocket 전환
- [ ] 1.1 WebSocketFrameHandler 작성 (BinaryWebSocketFrame <-> ByteBuf)
- [ ] 1.2 GamePipelineInitializer WebSocket 분기 추가 (/ws 경로)
- [ ] 1.3 WebSocket 연결 테스트 HTML 페이지

## Phase 2: 룸 시스템 수정 + Proto 확장
- [ ] 2.1 RoomComponent 최대 인원 2 제한
- [ ] 2.2 room.proto — ReqReadyGame, NotiGameStart, NotiGameEnd 추가
- [ ] 2.3 PlayerRoomController — ReqReadyGame 핸들러 (2인 준비 시 NotiGameStart)
- [ ] 2.4 world.proto 작성 (이동/게임세션 패킷)
- [ ] 2.5 combat.proto 작성 (PvE only)
- [ ] 2.6 character.proto 작성
- [ ] 2.7 chat.proto 작성
- [ ] 2.8 game_packet.proto oneof 업데이트

## Phase 3: RPG 서버 컴포넌트
- [ ] 3.1 CharacterComponent (스탯, 레벨, EXP)
- [ ] 3.2 PlayerWorldComponent (위치, 이동, 전투상태)
- [ ] 3.3 GameSessionComponent (룸 1:1, 몬스터 관리, 브로드캐스트 max 2인)
- [ ] 3.4 MonsterComponent (AI, 리스폰, 전투)
- [ ] 3.5 MonsterSystem (틱 기반 몬스터 관리)
- [ ] 3.6 CombatSystem (PvE 데미지, EXP 분배)
- [ ] 3.7 PlayerRpgController (ReqMove, ReqAttack PvE only, ReqChat)
- [ ] 3.8 LoginProcessor 수정 (CharacterComponent 초기화 연동)

## Phase 4: DB 확장
- [ ] 4.1 characters 테이블 추가 (schema_game.sql)
- [ ] 4.2 CharacterRow 클래스 작성
- [ ] 4.3 CharacterDbSet 작성 (Insert/Update/Select)
- [ ] 4.4 DatabaseSystem에 CharacterDbSet 등록

## Phase 5: HTML5 클라이언트
- [ ] 5.1 GameClient.Web 프로젝트 생성
- [ ] 5.2 protobuf.js 통합 + proto JS 생성
- [ ] 5.3 로그인 UI + WebSocket 연결
- [ ] 5.4 로비 UI (룸 목록, 생성, 입장)
- [ ] 5.5 룸 대기실 UI (인원 표시, Ready 버튼)
- [ ] 5.6 타일맵 렌더러 (Canvas)
- [ ] 5.7 플레이어/몬스터 렌더링 (스프라이트 + HP바)
- [ ] 5.8 이동 입력 처리 (WASD -> ReqMove -> NotiMove)
- [ ] 5.9 전투 UI — 클릭 공격(PvE), 데미지 수치, 몬스터 사망
- [ ] 5.10 채팅창 UI
- [ ] 5.11 스탯/레벨 UI (HP바, EXP바, 레벨)
- [ ] 5.12 결과 화면 (클리어/전멸, 로비 복귀)

## Phase 6: 테스트
- [ ] 6.1 RpgRoomScenario 부하 테스트 (Register -> Login -> CreateRoom -> ReadyGame -> Move -> Attack)
- [ ] 6.2 PveStressScenario (2인 쌍 다수 동시 룸)
- [ ] 6.3 Web REST API 엔드포인트 확인
