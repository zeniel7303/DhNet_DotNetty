# 무기 차별화 — 작업 체크리스트
Last Updated: 2026-03-25

## Phase 1: Proto 변경
- ✅ combat.proto: NotiCombat에 `int32 weapon_id = 4` 추가
- ✅ 빌드로 proto C# 재생성 확인

## Phase 2: WeaponSystem 반환 타입
- ✅ WeaponSystem.Tick 반환: `List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId, float PushX, float PushY)>`
- ✅ WeaponSystem.Tick 내부 results.Add에 weapon.Id + PushX/PushY 추가
- ✅ GameStage.ApplyWeaponHit 시그니처에 WeaponId, PushX, PushY 추가
- ✅ GameStage.Tick의 ApplyWeaponHit 호출부 업데이트
- ✅ NotiCombat 생성 시 WeaponId = (int)weaponId 포함

## Phase 3: 무기 판정 재구현
- ✅ GarlicWeapon: 1초 지속 오라 + 넉백(50px) 구현
- ✅ KnifeWeapon.TryAttack — Piercing Line (관통 직선, 폭 30f)
- ✅ AxeWeapon.TryAttack — Wide Arc (±60° 부채꼴, cos=0.5)
- ✅ WeaponHit record struct 추가 (MonsterId, Damage, PushX, PushY)
- ✅ MonsterComponent.Knockback() 메서드 추가

## Phase 4: 프로퍼티명 정리
- ✅ RoomComponent: `GameStage? GameSession` → `GameStage? Stage`
- ✅ PlayerRpgController: `.GameSession?` → `.Stage?`

## 완료 기준
- ✅ 빌드 경고 0, 오류 0
- ✅ NotiCombat에 weapon_id 포함 확인
- ✅ 커밋 완료

## Phase 5: 네이밍/Namespace 정리
- ✅ `WeaponSystem` → `WeaponManager` 클래스명 + 파일명 변경
- ✅ `MonsterComponent` namespace: `Stage` → `Stage.Monster`
- ✅ `GemManager` namespace: `Stage` → `Stage.Gem`
- ✅ `WaveSpawner` namespace: `Stage` → `Stage.Wave`
- ✅ 영향받는 파일들 `using` 추가 (WeaponBase, 무기 3종, GameStage, WaveSpawner)
- ✅ 빌드 경고 0, 오류 0

## 미커밋 변경사항
- 9개 파일 수정 (WeaponManager.cs 리네임 포함) — 커밋 대기 중

## 작업 완료
