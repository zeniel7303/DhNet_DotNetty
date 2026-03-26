# 무기 밸런스 리워크 — 체크리스트
Last Updated: 2026-03-26

## 단검 (KnifeWeapon)
- ⬜ 투사체 속도 400 → 700 px/s
- ⬜ 기본 단검: 비관통 (첫 번째 적중 시 즉시 소멸 + NotiProjectileDestroy)
- ⬜ KnifeProjectile에 Piercing 필드 추가 (bool, 기본 false)
- ⬜ MoveProjectiles에서 비관통 시 첫 적중 → 루프 break + 소멸 처리
- ⬜ KnifeWeapon에 _piercing 인스턴스 필드 추가 (미래 업그레이드용 보존)

## 도끼 (AxeWeapon)
- ⬜ 단일 Angle → List<float> _angles (다중 도끼 지원)
- ⬜ 홀수 레벨(3,5,7…) → 도끼 개수 +1, 각도 균등 재배치
- ⬜ 짝수 레벨(2,4,6…) → 데미지 증가 (기존 *1.2f)
- ⬜ 공전 속도 업그레이드 로직 제거 (레벨 기준 변경)
- ⬜ WeaponManager: axe.Angles 순회하여 OrbitalWeaponInfo 다중 생성
- ⬜ Angles public property 노출 (WeaponManager가 읽을 수 있도록)

## 마늘 (GarlicWeapon)
- ⬜ OnUpgrade에서 _knockbackDist 증가 제거
- ⬜ 데미지 + 반경만 업그레이드 (base.OnUpgrade의 Damage 증가 + _radius 증가)

## 빌드 검증
- ⬜ 컴파일 오류 0
