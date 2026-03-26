# 코드 리뷰 — 무기 리워크 (단검 투사체 / 도끼 공전)

작성일: 2026-03-26
리뷰 대상: WeaponBase, KnifeWeapon, AxeWeapon, WeaponManager, GameStage(무기 관련), combat.proto, game_packet.proto

---

## 요약

전반적인 구조는 건전하다. GameStage._stateLock 하에서 모든 Tick이 처리되므로 WeaponBase 계층의
동시성 모델은 올바르게 설계되어 있다. 다만 몇 가지 버그와 설계 문제가 발견되었다.

---

## CRITICAL

### [CRITICAL-1] KnifeWeapon: record struct + HashSet 변형 — 변경이 유실됨

**파일**: `KnifeWeapon.cs` 57–96행

`KnifeProjectile`은 `record struct`로 선언되어 있다.
`MoveProjectiles` 내부에서 `var p = _projectiles[i]`로 복사본을 가져온 뒤 `p.HitMonsters.Add(...)` 를 호출한다.

```csharp
var p = _projectiles[i];          // 값 복사
// ...
p.HitMonsters.Add(m.MonsterId);   // 복사본의 참조형 필드를 수정
// ...
_projectiles[i] = p with { X = nx, Y = ny, Elapsed = elapsed };
```

`HashSet<ulong>`은 참조형이므로 `p.HitMonsters`와 `_projectiles[i].HitMonsters`는 같은 객체를 가리킨다.
따라서 `Add`는 실제로 작동하지만, **`record struct`를 `with`로 재조립할 때 `HitMonsters` 참조가 그대로
넘어가는 것에 의존**하는 암묵적인 코드다.

더 심각한 문제: `p with { X = nx, Y = ny, Elapsed = elapsed }` 에서 `HitMonsters` 필드는 명시되지 않으므로
원본 참조가 유지된다. 이는 현재 우연히 동작하지만, 미래에 `record struct`의 복사 언어 사양이나
리팩토링 시 예측하기 어려운 버그로 이어진다.

**권장**: `KnifeProjectile`을 `class`로 변경하거나, `HitMonsters`를 별도 `Dictionary<long, HashSet<ulong>>`으로
투사체 ID 기준으로 관리한다.

---

### [CRITICAL-2] KnifeWeapon: _pendingPackets 참조 반환 — 외부 Clear와 경합

**파일**: `KnifeWeapon.cs` 145행

```csharp
public override List<GamePacket> GetPendingPackets() => _pendingPackets;
```

`WeaponManager.Tick`에서 `weapon.GetPendingPackets()`를 순회하는 동안,
같은 틱에서 `KnifeWeapon.Tick` 내부의 `_pendingPackets.Clear()`가 이미 호출되었으므로
순서 자체는 안전하다. 그러나 반환값이 내부 `List<>` 참조 자체이기 때문에:

- `WeaponManager`가 이 참조를 `packets` 리스트에 추가한 이후 다음 틱에서 `Clear()`가 호출되면
  이미 `packets`에 복사된 항목에는 영향이 없지만,
- **`packets.Add(pkt)`로 개별 항목을 복사하는 현재 코드(`foreach (var pkt in weapon.GetPendingPackets())`)는
  안전**하다.

실질적인 버그는 없으나, `GetPendingPackets()`가 내부 리스트 참조를 그대로 노출하는 것은
`WeaponBase`의 계약이 불분명함을 의미한다. 미래 구현체가 반환된 리스트를 캐시하거나
틱 간에 재사용하면 버그가 생긴다.

**권장**: `GetPendingPackets()`의 반환 타입을 `IReadOnlyList<GamePacket>`으로 변경하거나,
`WeaponManager`에서 호출 후 즉시 소비(enumerate)하고 참조를 보관하지 않도록 주석으로 명시한다.

---

## HIGH

### [HIGH-1] KnifeWeapon: 동일 틱에 투사체 이동 → base.Tick 순서 — 신규 투사체가 이번 틱에 즉시 충돌 판정됨

**파일**: `KnifeWeapon.cs` 39–55행

```csharp
// 1. 기존 투사체 이동 + 충돌 판정
MoveProjectiles(dt, monsterList, hits);

// 2. 쿨다운 기반 신규 투사체 발사
var spawnHits = base.Tick(dt, ownerX, ownerY, monsterList);
```

`base.Tick`이 `TryAttack`을 통해 새 투사체를 `_projectiles`에 추가한다.
이 새 투사체는 이번 틱의 `MoveProjectiles` 이후에 추가되므로 **이번 틱에는 이동하지 않는다**.
이는 의도된 동작으로 보이나, 문서화가 없어 나중에 순서를 바꾸면 동일 틱 즉시 충돌이 발생할 수 있다.

또한 `base.Tick` 내부에서 `_elapsed`를 관리하고 있어, `KnifeWeapon.Tick`이 `base.Tick`을
**무조건 호출**해야 쿨다운이 처리된다는 암묵적 의존이 있다. `base.Tick`을 호출하지 않으면
쿨다운이 전혀 진행되지 않는다.

**권장**: 투사체 처리 순서를 주석으로 명확히 문서화한다. 또는 쿨다운 전진(`_elapsed += dt`)을
`WeaponBase`의 별도 메서드로 분리하여 `virtual Tick`의 진입부에서 명시적으로 호출하도록 한다.

---

### [HIGH-2] AxeWeapon: _enemyCooldowns Dictionary를 순회 중 수정

**파일**: `AxeWeapon.cs` 44–54행

```csharp
foreach (var (id, elapsed) in _enemyCooldowns)
{
    float newElapsed = elapsed + dt;
    if (newElapsed >= PerEnemyCooldown)
        expired.Add(id);
    else
        _enemyCooldowns[id] = newElapsed;   // ← 순회 중 딕셔너리 수정
}
```

C#에서 `Dictionary`를 `foreach`로 순회하는 도중 값을 **수정**(`_enemyCooldowns[id] = ...`)하는 것은
기술적으로 키 추가/삭제가 아니므로 `InvalidOperationException`이 발생하지는 않는다.
그러나 이 동작은 공식 문서에서 보장하지 않으며, .NET 런타임 구현 변경 시 깨질 수 있다.

**권장**: 경과 시간을 별도 `Dictionary<ulong, float>`에 새로 기록하거나,
순회 후 업데이트할 목록을 따로 모아 적용한다.

---

### [HIGH-3] AxeWeapon: AngularSpeed const — 업그레이드 주석과 실제 코드 불일치

**파일**: `AxeWeapon.cs` 79–85행

```csharp
protected override void OnUpgrade()
{
    Damage = (int)(Damage * 1.2f);
    // 업그레이드 시 공전 속도 10% 증가
    // AngularSpeed는 const이므로 별도 필드로 관리하지 않고
    // 레벨에 비례하여 계산 (현재는 단순 데미지 증가만)
}
```

주석에 "업그레이드 시 공전 속도 10% 증가"라고 명시되어 있으나 실제로는 구현되어 있지 않다.
이는 기획 스펙과 코드 불일치로, 클라이언트 팀에 혼동을 줄 수 있다.

**권장**: `AngularSpeed`를 `private float _angularSpeed = MathF.PI;` 인스턴스 필드로 변경하고
`OnUpgrade`에서 실제로 10% 증가를 적용하거나, 주석에서 미구현임을 명확히 표시한다(`// TODO:`).

---

### [HIGH-4] WeaponManager: proto 필드 직접 변형 — 불변성 위반

**파일**: `WeaponManager.cs` 124–126행

```csharp
if (pkt.NotiProjectileSpawn != null)
    pkt.NotiProjectileSpawn.OwnerId = player.AccountId;
packets.Add(pkt);
```

`KnifeWeapon`이 생성한 `GamePacket`의 `NotiProjectileSpawn`을 `WeaponManager`가 직접 수정한다.
Protobuf 메시지 객체는 기본적으로 mutable이므로 런타임 오류는 없으나,
`weapon.GetPendingPackets()`가 내부 `_pendingPackets` 리스트 참조를 반환하기 때문에
**이미 생성된 패킷 객체가 외부에서 변형**되는 구조다.

만약 동일한 `GamePacket` 인스턴스가 두 개 이상의 플레이어의 `GetPendingPackets()`에서 참조된다면
(`List.Add`로 공유 참조가 가능한 경우) 마지막으로 수정한 `OwnerId`가 덮어씌워진다.
현재는 각 플레이어의 `KnifeWeapon`이 독립적이므로 공유 인스턴스 문제는 없지만 설계상 위험하다.

**권장**: `TryAttack`에서 `OwnerId`를 임시 0으로 두지 말고, `WeaponManager`가 `OwnerId`를
주입할 수 있도록 `GetPendingPackets(ulong ownerId)` 시그니처로 변경하거나,
패킷 생성 시점에 `OwnerId`를 알 수 없다면 팩토리 패턴으로 지연 생성한다.

---

## MEDIUM

### [MEDIUM-1] KnifeWeapon: 투사체 수 상한 없음 — 메모리 무제한 증가

**파일**: `KnifeWeapon.cs` 30, 123–129행

쿨다운 1초, 수명 1.5초 기준으로 최대 2개의 투사체가 동시에 존재해야 한다.
그러나 `_projectiles` 리스트에 최대 개수 제한이 없다.
업그레이드로 쿨다운이 0.3초까지 줄어들면 동시에 최대 5개까지 존재할 수 있다.
서버 이상 상태(빠른 틱, 타이밍 이상)에서 투사체가 누적될 경우 제한이 없다.

**권장**: `const int MaxProjectiles = 10;` 등 상한을 두고 초과 시 발사를 스킵하거나
가장 오래된 투사체를 제거한다.

---

### [MEDIUM-2] KnifeWeapon: 투사체 적중 시 NotiProjectileDestroy 미발송

**파일**: `KnifeWeapon.cs` 71–80행

투사체가 수명 만료로 소멸할 때는 `NotiProjectileDestroy`를 발송하지만,
관통 공격 중 모든 적을 이미 맞혔거나, 특정 조건에서 즉시 소멸해야 할 때의 처리가 없다.
현재 구현은 관통형이므로 수명이 다할 때까지 계속 날아가는데,
클라이언트가 `NotiCombat.projectile_id`를 수신해도 서버가 투사체를 소멸시키지 않으면
클라이언트는 투사체가 언제 사라지는지 `NotiProjectileDestroy`를 기다려야 한다.

이 부분은 클라이언트-서버 프로토콜 계약을 문서화할 필요가 있다.
"투사체는 수명 만료 시에만 `NotiProjectileDestroy`로 제거된다"는 것이 의도라면 명시해야 한다.

---

### [MEDIUM-3] WeaponManager: GetPrimaryWeaponId — 플레이어 공격 시 무기 ID 부정확

**파일**: `WeaponManager.cs` 37–40행, `GameStage.cs` 340행

```csharp
// GameStage.ProcessAttack 내부
WeaponId = (int)_weaponManager.GetPrimaryWeaponId(player.AccountId)
```

플레이어가 수동으로 `ReqAttack`을 보내는 경우 첫 번째 무기 ID를 `NotiCombat.weapon_id`에 사용한다.
그러나 수동 공격은 특정 무기와 연결되지 않은 기본 근접 공격일 수 있다.
첫 번째 무기가 `AxeWeapon`이라면 클라이언트는 도끼 이펙트를 재생하게 된다.

**권장**: 수동 공격용 `weapon_id`를 0(없음) 또는 별도 enum 값(`MeleeAttack`)으로 정의하거나,
`WeaponId` enum에 `None = -1` 또는 `Melee = 99` 등을 추가한다.

---

### [MEDIUM-4] AxeWeapon: _enemyCooldowns — 죽은 몬스터 항목 미정리

**파일**: `AxeWeapon.cs` 44–54행

몬스터가 사망해도 `_enemyCooldowns`에서 항목이 제거되지 않는다.
쿨다운이 만료되면 자동으로 제거되므로 최대 `PerEnemyCooldown(0.5초)` 후에는 정리된다.
게임이 수백 마리의 몬스터를 처리하는 경우, 매 틱 `_enemyCooldowns` 전체를 순회하는 비용이
누적될 수 있다. 웨이브가 계속 늘어날수록 딕셔너리 크기가 일시적으로 커진다.

현재 규모에서는 크게 문제되지 않으나, 대규모 웨이브(수백 마리)에서는 주의가 필요하다.

---

### [MEDIUM-5] WeaponBase.Tick — _elapsed 누산: dt 고정(0.1f) 가정에 의존

**파일**: `WeaponBase.cs` 37–42행

```csharp
_elapsed -= CooldownSec; // 초과분 이월 — 틱 타이밍 오차 방지
```

`_elapsed -= CooldownSec`을 사용하여 초과분을 이월하는 것은 올바른 패턴이다.
그러나 한 틱에서 `_elapsed`가 `CooldownSec * 2`를 초과할 경우(틱이 두 번 밀렸을 때)
한 번만 공격이 발생한다. 현재 `RunTickAsync`가 `PeriodicTimer(100ms)`를 사용하므로
정상 조건에서는 문제없지만, 시스템 부하로 틱이 밀리면 공격 기회를 잃는다.

이는 의도된 동작(틱당 최대 1회 공격)이라면 명시하고, 아니라면 `while (_elapsed >= CooldownSec)` 루프로 변경해야 한다.

---

## LOW

### [LOW-1] KnifeWeapon: NextProjectileId — static 시퀀스, 서버 재시작 시 ID 재사용 없음

**파일**: `KnifeWeapon.cs` 17–18행

`_projectileIdSeq`가 `static`이므로 서버가 재시작되면 0부터 다시 시작한다.
클라이언트가 이전 세션의 투사체 ID와 충돌할 가능성이 있다.
현재는 각 게임 세션이 독립적으로 시작되므로 실질적인 충돌 가능성은 낮다.

`GameStage._monsterIdSeq`와 동일한 패턴이므로 일관성은 있다.

---

### [LOW-2] WeaponBase: Dist 메서드 — 미사용

**파일**: `WeaponBase.cs` 64–65행

```csharp
protected static float Dist(float ax, float ay, float bx, float by)
    => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
```

현재 모든 하위 클래스가 `DistSq`만 사용하며 `Dist`는 사용되지 않는다.
사용하지 않는 유틸리티 메서드는 혼동을 줄 수 있다.

**권장**: 사용 시점까지 제거하거나 `// 직선 거리가 필요한 무기 구현 시 사용` 주석을 추가한다.

---

### [LOW-3] AxeWeapon: TryAttack — 사문화된 추상 메서드 구현

**파일**: `AxeWeapon.cs` 74–77행

```csharp
// WeaponBase.Tick을 완전히 override했으므로 TryAttack은 호출되지 않음
protected override List<WeaponHit> TryAttack(...) => [];
```

`WeaponBase`가 `TryAttack`을 `abstract`로 강제하므로 구현해야 하지만,
`AxeWeapon`에서는 절대 호출되지 않는다. 이 구조는 `WeaponBase` 설계의 한계를 드러낸다.

**권장**: `WeaponBase`에서 `TryAttack`을 `abstract` 대신 `virtual`로 변경하고
기본 구현을 `return [];`으로 제공하여 공전 무기가 빈 메서드를 강제로 구현하지 않아도 되도록 한다.
또는 `IProjectileWeapon` / `IOrbitalWeapon` 인터페이스를 분리한다.

---

### [LOW-4] combat.proto: NotiProjectileDestroy — 소멸 위치 없음

**파일**: `combat.proto` 16행

```protobuf
message NotiProjectileDestroy { uint64 projectile_id = 1; }
```

수명 만료로 소멸할 때 마지막 위치 정보(x, y)가 없다.
클라이언트는 투사체의 현재 시뮬레이션 위치에서 소멸 이펙트를 재생해야 하는데,
클라이언트 시뮬레이션과 서버 시뮬레이션이 미세하게 달라질 수 있다.

현재 클라이언트가 자체 투사체 시뮬레이션을 하지 않는다면 문제없다.
클라이언트가 서버 패킷 기반으로만 위치를 갱신한다면 `NotiProjectileMove` 패킷이 없어
투사체 위치 동기화가 불가능하다는 별도 문제가 있다.

---

## 프로토콜 검토

### combat.proto / game_packet.proto

- `NotiCombat.projectile_id` (uint64, 기본값 0): proto3에서 기본값 0은 "없음"을 의미하므로
  투사체 없는 공격과 구분 가능하다. 올바른 설계다.
- `NotiProjectileSpawn.owner_id`: 클라이언트가 어느 플레이어의 투사체인지 알 수 있어 적절하다.
- `OrbitalWeaponInfo.angle`: float 정밀도로 라디안 전송. 100ms 틱에서 각도 변화가 약 0.314rad이므로
  float 정밀도(약 7자리)로 충분하다.
- `game_packet.proto` 필드 번호 53–55: 연속적이고 예약 번호와 충돌 없음. 올바르다.

---

## 요약 테이블

| ID | 심각도 | 파일 | 내용 |
|----|--------|------|------|
| CRITICAL-1 | CRITICAL | KnifeWeapon.cs | record struct + HashSet 변형 — 암묵적 참조 의존 |
| CRITICAL-2 | CRITICAL | KnifeWeapon.cs | GetPendingPackets 내부 참조 노출 — 계약 불명확 |
| HIGH-1 | HIGH | KnifeWeapon.cs | base.Tick 암묵적 의존 + 발사 순서 미문서화 |
| HIGH-2 | HIGH | AxeWeapon.cs | foreach 중 Dictionary 값 수정 — 미보장 동작 |
| HIGH-3 | HIGH | AxeWeapon.cs | 업그레이드 주석과 실제 코드 불일치 |
| HIGH-4 | HIGH | WeaponManager.cs | 생성된 proto 패킷 객체 외부 직접 변형 |
| MEDIUM-1 | MEDIUM | KnifeWeapon.cs | 투사체 수 상한 없음 |
| MEDIUM-2 | MEDIUM | KnifeWeapon.cs | 적중 시 NotiProjectileDestroy 미발송 문서화 필요 |
| MEDIUM-3 | MEDIUM | WeaponManager.cs / GameStage.cs | 수동 공격 weapon_id 부정확 |
| MEDIUM-4 | MEDIUM | AxeWeapon.cs | 죽은 몬스터 쿨다운 항목 미정리 |
| MEDIUM-5 | MEDIUM | WeaponBase.cs | 멀티틱 밀림 시 공격 기회 유실 |
| LOW-1 | LOW | KnifeWeapon.cs | static 투사체 ID — 재시작 후 낮은 충돌 위험 |
| LOW-2 | LOW | WeaponBase.cs | 미사용 Dist 메서드 |
| LOW-3 | LOW | AxeWeapon.cs | 사문화된 abstract TryAttack 구현 강제 |
| LOW-4 | LOW | combat.proto | NotiProjectileDestroy에 마지막 위치 없음 |
