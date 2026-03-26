# 무기 리워크: 단검 투사체 / 도끼 공전 — 구현 계획
Last Updated: 2026-03-26

## 요약

| 무기 | 현재 | 변경 후 |
|------|------|---------|
| 단검(Knife) | 즉발 관통 직선 판정 | 투사체 발사 → 진행 중 관통 충돌 |
| 도끼(Axe) | 쿨다운 기반 부채꼴 즉발 | 플레이어 주변 공전, 닿는 적 지속 데미지 |

---

## 설계 상세

### 단검 — 투사체(Projectile)

- 쿨다운 만료 시 가장 가까운 적 방향으로 `KnifeProjectile` 생성
- 매 틱 투사체 위치 이동 (속도 400px/s)
- 이동 중 충돌 반경(20px) 내 적에 데미지 (관통 — 동일 적은 1회만)
- 최대 비행 거리 600px / 1.5초 수명
- 쿨다운 1.0s (원래 0.5s에서 상향 — 투사체 특성상 더 강력)
- 쿨다운 만료 시 활성 투사체가 이미 있어도 새로 발사

**프로토콜:**
- `NotiProjectileSpawn` — 발사 시: id, owner_id, weapon_id, x, y, vel_x, vel_y
- `NotiProjectileDestroy` — 수명 만료(미적중) 시: projectile_id
- `NotiCombat.projectile_id` (field 5 추가) — 적중 시 클라이언트가 해당 투사체 제거

### 도끼 — 공전(Orbit)

- `_angle` 상태 유지, 매 틱 `angle += AngularSpeed * dt` (1바퀴/2초 = π rad/s)
- 도끼 위치 = (ownerX + cos(angle) * OrbitRadius, ownerY + sin(angle) * OrbitRadius)
- OrbitRadius = 100px, HitRadius = 35px
- 적중 시 데미지(20), 동일 적 0.5s 쿨다운으로 연속 피격 방지
- 기존 WeaponBase 쿨다운 미사용 — `Tick`에서 직접 처리

**프로토콜:**
- `NotiOrbitalWeaponSync` — 매 틱 모든 공전 무기 각도 배치 브로드캐스트
- `OrbitalWeaponInfo` — owner_id, weapon_id, angle (클라이언트가 위치 + 회전 계산)

---

## 변경 파일 목록

### Proto
- `GameServer.Protocol/Protos/combat.proto` — 메시지 4개 추가, NotiCombat field 5 추가
- `GameServer.Protocol/Protos/game_packet.proto` — field 53~56 추가

### 서버
- `Weapons/WeaponBase.cs` — `GetPendingPackets()` 가상 메서드 추가
- `Weapons/KnifeWeapon.cs` — 투사체 시스템으로 전면 재작성
- `Weapons/AxeWeapon.cs` — 공전 시스템으로 전면 재작성
- `Weapons/WeaponManager.cs` — Tick에서 pending packets 수집 + 공전 sync 생성
- `Component/Stage/GameStage.cs` — weapon packets 브로드캐스트

---

## 파라미터

### KnifeWeapon
| 항목 | 값 |
|------|---|
| Damage | 15 |
| Cooldown | 1.0s |
| Speed | 400px/s |
| Lifetime | 1.5s |
| HitRadius | 20px |
| Piercing | O (동일 적 1회) |

### AxeWeapon
| 항목 | 값 |
|------|---|
| Damage | 20 |
| OrbitRadius | 100px |
| HitRadius | 35px |
| AngularSpeed | π rad/s (1바퀴/2초) |
| PerEnemyCooldown | 0.5s |
