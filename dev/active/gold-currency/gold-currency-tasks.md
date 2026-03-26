# 골드/인게임 데이터 정책 — 작업 체크리스트
Last Updated: 2026-03-26

## Phase 1: Proto
- ✅ character.proto: NotiGoldGain 메시지 추가
- ✅ game_packet.proto: NotiGoldGain = 52 추가

## Phase 2: DB 레이어
- ✅ CharacterRow: gold 필드만 포함 (인게임 스탯 제거)
- ✅ CharacterDbSet.UpsertAsync: gold만 저장 (INSERT ON DUPLICATE KEY UPDATE)
- ✅ CharacterDbSet.SelectAsync: gold만 로드
- ✅ schema_game.sql: characters 테이블 (account_id, gold)

## Phase 3: CharacterComponent
- ✅ Gold 프로퍼티 + AddGold(int) 메서드
- ✅ LoadFrom: gold만 로드
- ✅ ToRow: account_id + gold만 포함
- ✅ 생성자 기본값: Level=1, Exp=0, Hp=500, Attack=20, Defense=10 (항상 새로 시작)

## Phase 4: MonsterComponent + GameStage
- ✅ MonsterComponent: GoldReward 프로퍼티 (타입별 차등)
- ✅ GameStage.ApplyWeaponHit: GiveGold() 호출 + NotiGoldGain (해당 플레이어에게만)
- ✅ GameStage.ProcessAttack: GiveGold() 호출 + NotiGoldGain

## 완료 기준
- ✅ 빌드 경고 0, 오류 0
- ✅ 게임 종료 후 gold가 characters 테이블에 저장됨
- ✅ 재로그인 시 gold 복원, 나머지 스탯은 초기값

## 작업 완료
