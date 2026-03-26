# 무기 리워크 — 작업 체크리스트
Last Updated: 2026-03-26

## Phase 1: Proto
- ✅ combat.proto: NotiCombat field 5 (projectile_id) 추가
- ✅ combat.proto: NotiProjectileSpawn, NotiProjectileDestroy, OrbitalWeaponInfo, NotiOrbitalWeaponSync 추가
- ✅ game_packet.proto: field 53~55 추가 (noti_projectile_spawn=53, noti_projectile_destroy=54, noti_orbital_weapon_sync=55)
- ✅ 빌드로 proto C# 재생성 확인

## Phase 2: WeaponBase 확장
- ✅ GetPendingPackets() 가상 메서드 추가
- ✅ Tick() virtual로 변경 (KnifeWeapon/AxeWeapon override 허용)
- ✅ WeaponHit에 ProjectileId 필드 추가 (ulong, 기본값 0)

## Phase 3: KnifeWeapon 재작성
- ✅ KnifeProjectile record struct 정의 (Id, X, Y, VelX, VelY, Elapsed, HitMonsters)
- ✅ 투사체 발사 로직 (TryAttack → NotiProjectileSpawn 패킷 + projectile 상태 추가)
- ✅ 매 틱 투사체 이동 + 충돌 판정 (관통, 동일 적 HashSet 중복 방지)
- ✅ 수명 만료 시 NotiProjectileDestroy 패킷 생성
- ✅ GetPendingPackets()로 Spawn/Destroy 패킷 반환

## Phase 4: AxeWeapon 재작성
- ✅ Angle 공전 상태 관리 (public float Angle)
- ✅ 매 틱 각도 갱신 + 위치 계산 + 충돌
- ✅ 적별 피격 쿨다운 딕셔너리 (_enemyCooldowns)
- ✅ WeaponBase.Tick 완전 override (쿨다운 미사용)

## Phase 5: WeaponManager + GameStage 연동
- ✅ WeaponManager.Tick 반환형 변경 → (Hits, Packets) 튜플
- ✅ Tick에서 GetPendingPackets() 수집 + NotiProjectileSpawn.OwnerId 주입
- ✅ 공전 무기 Angle 수집 → NotiOrbitalWeaponSync 생성
- ✅ WeaponManager.cs에 using GameServer.Protocol 추가
- ✅ GameStage.Tick에서 weapon packets pending에 AddRange
- ✅ GameStage.ApplyWeaponHit에 projectileId 파라미터 추가 → NotiCombat.ProjectileId 설정

## 완료 기준
- ✅ 빌드 컴파일 오류 0 (CS 오류 없음 확인)
- ⬜ 단검 투사체 NotiProjectileSpawn 포함 확인 (런타임 테스트)
- ⬜ 도끼 NotiOrbitalWeaponSync 매 틱 발행 확인 (런타임 테스트)

## 상태
**구현 완료. 런타임 테스트 및 클라이언트 연동 미완료.**
