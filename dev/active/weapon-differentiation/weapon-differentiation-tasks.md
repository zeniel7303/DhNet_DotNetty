# 무기 차별화 — 작업 체크리스트
Last Updated: 2026-03-25

## Phase 1: Proto 변경
- [ ] combat.proto: NotiCombat에 `int32 weapon_id = 4` 추가
- [ ] 빌드로 proto C# 재생성 확인

## Phase 2: WeaponSystem 반환 타입
- [ ] WeaponSystem.Tick 반환: `List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId)>`
- [ ] WeaponSystem.Tick 내부 results.Add에 weapon.Id 추가
- [ ] GameStage.ApplyWeaponHit 시그니처에 WeaponId 추가
- [ ] GameStage.Tick의 ApplyWeaponHit 호출부 업데이트
- [ ] NotiCombat 생성 시 WeaponId = (int)weaponId 포함

## Phase 3: 무기 판정 재구현
- [ ] KnifeWeapon.TryAttack — Piercing Line (관통 직선)
- [ ] AxeWeapon.TryAttack — Wide Arc (120° 부채꼴)

## Phase 4: 프로퍼티명 정리
- [ ] RoomComponent: `GameStage? GameSession` → `GameStage? Stage`
- [ ] PlayerRpgController: `.GameSession?` → `.Stage?`

## 완료 기준
- [ ] 빌드 경고 0, 오류 0
- [ ] Knife: 일렬 배치 몬스터 다수 적중 확인 (코드 리뷰)
- [ ] Axe: 120° 내 다수 몬스터 적중 확인 (코드 리뷰)
- [ ] NotiCombat에 weapon_id 포함 확인
