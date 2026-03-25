# 무기 차별화 구현 계획 (Vampire Survivors 레퍼런스)
Last Updated: 2026-03-25

## Executive Summary

현재 KnifeWeapon과 AxeWeapon은 수치만 다를 뿐 동일한 "가장 가까운 단일 타겟" 로직을 사용한다.
뱀파이어 서바이벌을 레퍼런스로 각 무기에 고유한 판정 메커니즘을 구현하고,
클라이언트가 적절한 이펙트를 재생할 수 있도록 NotiCombat에 weapon_id를 추가한다.
부수적으로 RoomComponent.GameSession 프로퍼티명을 Stage로 정리한다.

## Current State Analysis

### 무기 현황
| 무기 | 현재 로직 | 문제점 |
|------|-----------|--------|
| GarlicWeapon | 반경 80 내 모든 몬스터 AoE (3s 쿨) | VS 원작과 유사, 큰 문제 없음 |
| KnifeWeapon | 가장 가까운 단일 몬스터 (0.5s 쿨) | 단순 단일타겟, 투사체 개념 없음 |
| AxeWeapon | 가장 가까운 단일 몬스터 (1.5s 쿨) | KnifeWeapon과 로직 완전 동일 |

### 데이터 흐름
```
WeaponSystem.Tick() → List<(AttackerId, MonsterId, Damage)>
→ GameStage.ApplyWeaponHit()
→ NotiCombat { AttackerPlayerId, TargetMonsterId, Damage }
```
weapon_id가 없어 클라이언트는 어떤 무기로 때렸는지 알 수 없음.

### 기타 문제
- `RoomComponent.GameStage? GameSession` — 타입명(GameStage)과 프로퍼티명(GameSession) 불일치

## Proposed Future State

### 무기별 고유 메커니즘 (VS 레퍼런스)

#### Garlic (마늘) — Aura
- 변경 없음. 반경 내 전체 AoE, 쿨 3s
- 업그레이드 시 반경 +10 (기존 유지)

#### Knife (단검) — Piercing Line
- VS 원작: 가장 가까운 적 방향으로 투사체 발사, 관통
- 서버 구현: 플레이어 → 가장 가까운 적 방향으로 선(ray)을 그어 선과의 수직 거리 < 30f이고 앞방향인 모든 몬스터 적중
- 빠른 쿨다운(0.5s), 낮은 데미지(10), 관통으로 다수 처치 가능

#### Axe (도끼) — Wide Arc
- VS 원작: 위로 던져 포물선 후 착지, 넓은 범위 타격
- 서버 구현: 가장 가까운 적 방향 기준 좌우 ±60° (총 120°) 원호 내 모든 몬스터 적중
- 느린 쿨다운(2s), 높은 데미지(25), 범위 공격

### 프로토콜 변경
`NotiCombat`에 `weapon_id` 필드 추가 (클라이언트 이펙트 재생용)

### 데이터 흐름 변경
```
WeaponSystem.Tick() → List<(AttackerId, MonsterId, Damage, WeaponId)>  // WeaponId 추가
→ GameStage.ApplyWeaponHit(weaponId 포함)
→ NotiCombat { AttackerPlayerId, TargetMonsterId, Damage, WeaponId }
```

## Implementation Phases

### Phase 1: Proto 변경 [S]
- `combat.proto`: NotiCombat에 `int32 weapon_id = 4` 추가
- protobuf 재생성

### Phase 2: WeaponSystem 반환 타입 변경 [S]
- `WeaponSystem.Tick()` 반환: `List<(ulong, ulong, int, WeaponId)>` — WeaponId 추가
- `GameStage.ApplyWeaponHit()` 시그니처에 WeaponId 추가
- NotiCombat 생성 시 WeaponId 포함

### Phase 3: 무기 판정 로직 재구현 [M]
- `KnifeWeapon.TryAttack` — Piercing Line 구현
- `AxeWeapon.TryAttack` — Wide Arc 구현

### Phase 4: 프로퍼티명 정리 [S]
- `RoomComponent.GameSession` → `Stage`
- `PlayerRpgController`에서 참조 업데이트

## Detailed Tasks

### Phase 1
- [ ] combat.proto NotiCombat에 weapon_id 필드 추가
- [ ] dotnet build로 proto 재생성 확인

### Phase 2
- [ ] WeaponSystem.Tick 반환 타입에 WeaponId 추가
- [ ] GameStage.ApplyWeaponHit(ulong attackerId, ulong monsterId, int damage, WeaponId weaponId) 시그니처 변경
- [ ] NotiCombat 생성 코드에 WeaponId = (int)weaponId 추가
- [ ] WeaponSystem.Tick 내부: weapon.Id를 결과 튜플에 포함

### Phase 3
- [ ] KnifeWeapon.TryAttack: Ray casting — 방향 벡터 정규화 후 각 몬스터의 수직 거리 계산
- [ ] AxeWeapon.TryAttack: Arc판정 — 방향 벡터 기준 내적/외적으로 ±60° 내 몬스터 필터
- [ ] WeaponBase에 헬퍼 추가: GetNearestAlive(), GetDirectionTo()

### Phase 4
- [ ] RoomComponent: `public GameStage? GameSession` → `public GameStage? Stage`
- [ ] PlayerRpgController: `CurrentRoom?.GameSession?` → `CurrentRoom?.Stage?`
- [ ] 빌드 확인

## Risk Assessment

| 위험 | 완화 |
|------|------|
| proto 변경 시 기존 클라이언트 호환성 | weapon_id는 신규 필드, protobuf는 기본값(0) 하위 호환 |
| Arc 판정 수치 조정 필요 | 각도(60°), 범위(300f) 상수로 분리하여 조정 용이하게 |
| Zero-vector division (플레이어 위치 = 몬스터 위치) | 방향 벡터 길이 < epsilon 시 공격 스킵 처리 |

## Success Metrics
- 세 무기가 각기 다른 판정 영역을 가짐
- Knife: 여러 몬스터가 일렬로 있을 때 모두 적중
- Axe: 넓은 부채꼴 범위 내 다수 적중
- NotiCombat.weapon_id로 클라이언트가 이펙트 구분 가능
- 빌드 경고 0, 오류 0
