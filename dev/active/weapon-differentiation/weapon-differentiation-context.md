# 무기 차별화 — 컨텍스트
Last Updated: 2026-03-25

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
