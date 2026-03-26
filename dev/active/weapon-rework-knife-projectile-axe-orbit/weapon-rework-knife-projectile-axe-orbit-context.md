# 무기 리워크 컨텍스트
Last Updated: 2026-03-26

## 현재 구현 상태

**Phase 1~5 모두 완료. 컴파일 오류 0.**

모든 서버 코드가 작성되었고, `dotnet build GameServer/GameServer.csproj` 에서 CS 오류 없음 확인.
(DLL 잠금 경고는 서버가 실행 중이어서 발생 — 코드 문제 아님)

---

## 수정된 파일 및 이유

### GameServer.Protocol/Protos/combat.proto
- `NotiCombat`에 `uint64 projectile_id = 5` 추가 — 투사체 적중 시 클라이언트가 해당 투사체 제거
- `NotiProjectileSpawn` — 투사체 발사 이벤트 (id, owner_id, weapon_id, x, y, vel_x, vel_y)
- `NotiProjectileDestroy` — 수명 만료 이벤트 (projectile_id)
- `OrbitalWeaponInfo`, `NotiOrbitalWeaponSync` — 도끼 각도 매 틱 배치 브로드캐스트

### GameServer.Protocol/Protos/game_packet.proto
- field 53: `noti_projectile_spawn`
- field 54: `noti_projectile_destroy`
- field 55: `noti_orbital_weapon_sync`

### GameServer/Component/Stage/Weapons/WeaponBase.cs
- `WeaponHit` 레코드에 `ulong ProjectileId = 0` 추가 (기본값 0 = 투사체 없음)
- `Tick()` 메서드를 `virtual`로 변경 — KnifeWeapon/AxeWeapon이 override 가능하게
- `GetPendingPackets()` 가상 메서드 추가 — 무기가 직접 생성한 패킷 반환

### GameServer/Component/Stage/Weapons/KnifeWeapon.cs (전면 재작성)
- `KnifeProjectile` record struct: Id, X, Y, VelX, VelY, Elapsed, HitMonsters(HashSet)
- `_projectiles`: 활성 투사체 목록
- `_pendingPackets`: 이번 틱 발생 패킷 (Spawn/Destroy)
- `Tick()` override: 기존 투사체 이동+충돌 먼저, 그 다음 base.Tick() 호출해 새 투사체 발사
- `TryAttack()`: 가장 가까운 몬스터 방향으로 투사체 발사, NotiProjectileSpawn 패킷 생성
- `GetPendingPackets()` override: `_pendingPackets` 반환

### GameServer/Component/Stage/Weapons/AxeWeapon.cs (전면 재작성)
- `public float Angle { get; private set; }` — WeaponManager가 읽어 NotiOrbitalWeaponSync 생성
- `Tick()` override: 각도 진행 → 도끼 월드 위치 계산 → 충돌 판정 + 적별 쿨다운
- `_enemyCooldowns`: 동일 적 0.5초 재피격 방지 (Dictionary<ulong, float>)
- `TryAttack()`: 빈 구현 (Tick 완전 override로 호출되지 않음)

### GameServer/Component/Stage/Weapons/WeaponManager.cs
- `using GameServer.Protocol` 추가
- `Tick()` 반환형 변경:
  ```
  (List<(ulong AttackerId, ulong MonsterId, int Damage, WeaponId WeaponId, float PushX, float PushY, ulong ProjectileId)> Hits,
   List<GamePacket> Packets)
  ```
- 각 무기의 `GetPendingPackets()` 수집, `NotiProjectileSpawn.OwnerId = player.AccountId` 주입
- `AxeWeapon` 감지 → `OrbitalWeaponInfo` 생성 → `NotiOrbitalWeaponSync` 패킷 추가

### GameServer/Component/Stage/GameStage.cs
- 무기 틱 결과 분해: `var (weaponHits, weaponPackets) = _weaponManager.Tick(...)`
- `pending.AddRange(weaponPackets)` — 투사체/공전 패킷 브로드캐스트
- `ApplyWeaponHit` 시그니처에 `ulong projectileId` 추가 → `NotiCombat.ProjectileId` 설정

---

## 주요 결정사항

### OwnerId 주입 위치
KnifeWeapon 자체는 owner_id를 모름 → WeaponManager.Tick에서 player.AccountId를 주입.
`pkt.NotiProjectileSpawn.OwnerId = player.AccountId` (proto 생성 클래스는 mutable)

### WeaponBase.Tick을 virtual로 변경한 이유
AxeWeapon은 쿨다운 개념이 없어서 WeaponBase.Tick의 쿨다운 처리를 완전히 우회해야 함.
KnifeWeapon은 투사체 이동을 먼저 처리한 뒤 base.Tick()을 호출해 발사를 트리거.

### WeaponHit에 ProjectileId 추가
투사체 적중 시 NotiCombat.ProjectileId를 통해 클라이언트가 투사체를 제거할 수 있도록.
0이면 투사체 없음 (마늘/도끼 등 즉발 무기).

---

## 다음 단계 (런타임 검증)

1. 서버 재시작 후 클라이언트 연결
2. 단검 장착 상태에서 `NotiProjectileSpawn`/`NotiCombat.ProjectileId` 수신 확인
3. 도끼 장착 상태에서 `NotiOrbitalWeaponSync` 매 틱 수신 확인
4. 클라이언트 렌더링 구현 (서버와는 독립적)
   - 단검: SpawnProjectile(id, ownerPos, vel) → 매 틱 위치 extrapolation
   - 도끼: OnOrbitalSync(angle) → cos/sin으로 위치 + 이미지 회전

---

## 미완료 / 남은 작업 없음

서버 구현은 완전히 완료됨. 이후 작업은 클라이언트 사이드 렌더링.
