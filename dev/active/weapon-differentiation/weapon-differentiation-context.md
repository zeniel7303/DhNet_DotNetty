# 무기 차별화 — 컨텍스트
Last Updated: 2026-03-26 (네이밍/Namespace 정리)

## 핵심 파일

| 파일 | 역할 |
|------|------|
| `GameServer/Component/Stage/Weapons/WeaponBase.cs` | 추상 기반 클래스, Tick/TryAttack 구조 |
| `GameServer/Component/Stage/Weapons/KnifeWeapon.cs` | 재구현 대상 — Piercing Line |
| `GameServer/Component/Stage/Weapons/AxeWeapon.cs` | 재구현 대상 — Wide Arc |
| `GameServer/Component/Stage/Weapons/GarlicWeapon.cs` | 변경 없음 |
| `GameServer/Component/Stage/Weapons/WeaponSystem.cs` | Tick 반환 타입 변경 필요 |
| `GameServer/Component/Stage/GameStage.cs` | ApplyWeaponHit 시그니처 변경 |
| `GameServer.Protocol/Protos/combat.proto` | NotiCombat weapon_id 추가 |
| `GameServer/Component/Room/RoomComponent.cs` | GameSession → Stage 프로퍼티명 |
| `GameServer/Controllers/PlayerRpgController.cs` | CurrentRoom?.GameSession? → Stage? |

## 알고리즘 상세

### KnifeWeapon — Piercing Line
```
ownerX, ownerY = 플레이어 위치
nearest = 가장 가까운 살아있는 몬스터

if nearest == null → return []

// 투사 방향 벡터 (정규화)
dx = nearest.X - ownerX
dy = nearest.Y - ownerY
len = sqrt(dx*dx + dy*dy)
if len < 1f → return []   // zero-vector guard
ux = dx / len
uy = dy / len

hits = []
foreach monster in monsters:
    if !monster.IsAlive → skip
    // 몬스터까지의 벡터
    mx = monster.X - ownerX
    my = monster.Y - ownerY
    // 투영 (dot product) — 앞방향 판단
    dot = mx*ux + my*uy
    if dot < 0 → skip  // 뒤에 있음
    if dot > MaxRange(400f) → skip
    // 수직 거리 (cross product magnitude)
    perp = abs(mx*uy - my*ux)
    if perp <= KnifeWidth(30f) → hit
```

### AxeWeapon — Wide Arc
```
nearest = 가장 가까운 살아있는 몬스터
if nearest == null → return []

// 참조 방향
dx = nearest.X - ownerX
dy = nearest.Y - ownerY
len = sqrt(dx*dx + dy*dy)
if len < 1f → return []
ux = dx / len
uy = dy / len

hits = []
foreach monster in monsters:
    if !monster.IsAlive → skip
    mx = monster.X - ownerX
    my = monster.Y - ownerY
    dist = sqrt(mx*mx + my*my)
    if dist > MaxRange(300f) → skip
    if dist < 1f → hit  // 같은 위치면 무조건 적중
    // 단위 벡터
    nmx = mx / dist
    nmy = my / dist
    // 내적 = cos(angle)
    cosAngle = nmx*ux + nmy*uy
    if cosAngle >= cos(60°) = 0.5f → hit  // ±60° 이내
```

## 주요 결정사항

- `WeaponBase.Tick` 반환 타입은 변경 안 함 — `WeaponSystem`이 `weapon.Id`를 알고 있으므로 거기서 튜플에 추가
- GarlicWeapon 로직 변경 없음 — VS 원작과 이미 유사
- proto `weapon_id = 4` 필드 추가 (하위 호환, 기본값 0 = Garlic)
- Arc 각도 60°, 범위 300f, Knife 너비 30f — 상수로 분리

## 의존성
- protobuf 재생성: `dotnet build GameServer.Protocol/` 로 자동 처리

---

## 2026-03-26 세션 — 버그 수정

무기/몬스터/웨이브 코드에서 3개 버그 발견 및 수정. 빌드 경고 0 확인.

### 수정 1: 몬스터 리스폰 시 공격 쿨다운 미초기화 (`MonsterComponent.cs:90`)
- **문제**: `_attackElapsed`가 리스폰 시 초기화되지 않아 즉시 공격 가능
- **수정**: 리스폰 블록에 `_attackElapsed = 0;` 추가

### 수정 2: 웨이브 몬스터 캡 도달 시 `NotiWaveStart(MonsterCount=0)` 전송 (`GameStage.cs:302`)
- **문제**: `WaveSpawner.Tick`이 몬스터 200마리 한계 시 빈 리스트(null 아님) 반환 →
  `DoWaveSpawn`이 호출되어 `NotiWaveStart { MonsterCount = 0 }` 브로드캐스트
- **수정**: 조건을 `waveSpawns != null` → `waveSpawns is { Count: > 0 }` 으로 변경

### 수정 3: 유효하지 않은 `weaponId` 수신 시 마늘 추가 (`WeaponSystem.cs:77`)
- **문제**: `switch` default 케이스가 `new GarlicWeapon()` 반환 — 클라이언트가 임의 ID 전송 시 마늘 추가
- **수정**: default → `null` 반환 후 early return

### 기타: 낡은 주석 제거
- `MonsterComponent.cs`, `WaveSpawner.cs` 의 "Phase 6에서 3200x2400으로 확장 예정" 주석 삭제
  (이미 3200x2400 적용됨)

---

## 2026-03-26 세션 — 2차 버그 수정 (코드 리뷰 후속)

1차 수정 후 코드 아키텍처 리뷰에서 발견된 추가 문제 4건 수정. 빌드 경고 0 확인.

### 수정 1: `ShouldAttack()` 거리 판정 이후로 이동 (`GameStage.cs:240`)
- **문제**: `ShouldAttack()`이 거리 판정보다 먼저 호출되어 사거리 밖 몬스터의 쿨다운이 소모됨
- **수정**: 조건 순서 변경
  ```csharp
  // Before
  if (!monster.IsAlive || nearestPlayer == null || !monster.ShouldAttack()) continue;
  float attackRangeSq = ...;
  if (nearestDistSq > attackRangeSq) continue;
  // After
  if (!monster.IsAlive || nearestPlayer == null) continue;
  float attackRangeSq = ...;
  if (nearestDistSq > attackRangeSq) continue;
  if (!monster.ShouldAttack()) continue;
  ```

### 수정 2: `SendAsync` discard → `ContinueWith(OnlyOnFaulted)` (`GameStage.cs:417, 498`)
- **문제**: `_stateLock` 내부의 `_ = SendAsync(...)` 패턴 — UnobservedTaskException 발생 가능
- **수정**: `.ContinueWith(t => GameLogger.Error(...), TaskContinuationOptions.OnlyOnFaulted)` 추가
- **적용 위치**: `CollectGems`의 NotiExpGain, `GiveGold`의 NotiGoldGain

### 수정 3: 쿨다운 초과분 이월 (`WeaponBase.cs:38`)
- **문제**: `_elapsed = 0f` — 초과분이 버려져 틱 타이밍에 따른 미세 빈도 오차 발생
- **수정**: `_elapsed -= CooldownSec` — 초과분 이월로 일정한 공격 빈도 유지

### 수정 4: `WeaponPool` 인덱스 직접 접근 → `FirstOrDefault` (`WeaponSystem.cs:51`)
- **문제**: `WeaponPool[(int)w.Id]` — WeaponId 확장 시 `IndexOutOfRangeException` 크래시
- **수정**: `WeaponPool.FirstOrDefault(e => e.Id == w.Id).Name ?? w.Id.ToString()`

### 미수정: WaveSpawner 음수 스폰 좌표
- `SpawnMargin = 40f` 으로 맵 외부(-40 or MapSize+40) 스폰은 **의도된 설계** (오프스크린 진입 효과)
- 클라이언트가 음수 좌표를 처리하는 한 문제 없음. 수정하지 않음.

---

## 2026-03-26 세션 — 네이밍/Namespace 정리

### 배경
`WeaponSystem`이 `Component/Stage/Weapons/`에 있음에도 이름이 `System` — `Systems/` 전역 싱글턴들과 혼동 소지. 추가로 `GemManager`, `WaveSpawner`, `MonsterComponent`의 namespace가 폴더 경로를 반영하지 않는 문제도 동시 수정.

### 변경 내역

| 변경 | 파일 |
|------|------|
| `WeaponSystem` 클래스 → `WeaponManager`, 파일 리네임 | `WeaponSystem.cs` → `WeaponManager.cs` |
| namespace `...Stage` → `...Stage.Monster` | `MonsterComponent.cs` |
| namespace `...Stage` → `...Stage.Gem` | `GemManager.cs` |
| namespace `...Stage` → `...Stage.Wave` + `using ...Monster` 추가 | `WaveSpawner.cs` |
| `using ...Monster` 추가 | `WeaponBase.cs`, `GarlicWeapon.cs`, `KnifeWeapon.cs`, `AxeWeapon.cs` |
| `using ...Gem/Wave/Monster` 3개 추가, `_weaponSystem` → `_weaponManager` | `GameStage.cs` |

### 결정 사항
- `SessionComponent` (`Network/SessionComponent.cs`) 은 Network 레이어 전용으로 이름 유지 — 이동 불필요
- `WeaponSystem` 파일은 git mv 없이 일반 mv 사용 (git이 rename으로 추적)
- 빌드 경고 0, 오류 0 확인

### 현재 상태
- 작업 완료. 미커밋 상태.
- 수정 파일: 9개 (WeaponManager.cs 리네임 포함)
