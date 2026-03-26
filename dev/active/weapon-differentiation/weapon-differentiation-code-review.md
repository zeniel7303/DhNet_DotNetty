# weapon-differentiation 코드 리뷰

리뷰 일자: 2026-03-26
대상 세션: 네이밍/Namespace 정리 세션

## 리뷰 대상 파일

| 파일 | 변경 내용 |
|------|-----------|
| `Weapons/WeaponSystem.cs` → `Weapons/WeaponManager.cs` | 파일 리네임 + 클래스명 변경 |
| `Monster/MonsterComponent.cs` | namespace `Stage` → `Stage.Monster` |
| `Gem/GemManager.cs` | namespace `Stage` → `Stage.Gem` |
| `Wave/WaveSpawner.cs` | namespace `Stage` → `Stage.Wave`, `using ...Monster` 추가 |
| `Weapons/WeaponBase.cs` | `using ...Monster` 추가 |
| `Weapons/GarlicWeapon.cs` | `using ...Monster` 추가 |
| `Weapons/KnifeWeapon.cs` | `using ...Monster` 추가 |
| `Weapons/AxeWeapon.cs` | `using ...Monster` 추가 |
| `GameStage.cs` | using 3개 추가, `_weaponSystem` → `_weaponManager` |

---

## 1. Namespace 일관성 확인

### 1-1. 폴더-Namespace 매핑 현황

| 파일 경로 | 선언된 namespace | 일치 여부 |
|-----------|------------------|-----------|
| `Component/Stage/GameStage.cs` | `GameServer.Component.Stage` | 정상 |
| `Component/Stage/Monster/MonsterComponent.cs` | `GameServer.Component.Stage.Monster` | 정상 |
| `Component/Stage/Gem/GemManager.cs` | `GameServer.Component.Stage.Gem` | 정상 |
| `Component/Stage/Wave/WaveSpawner.cs` | `GameServer.Component.Stage.Wave` | 정상 |
| `Component/Stage/Weapons/WeaponBase.cs` | `GameServer.Component.Stage.Weapons` | 정상 |
| `Component/Stage/Weapons/WeaponManager.cs` | `GameServer.Component.Stage.Weapons` | 정상 |
| `Component/Stage/Weapons/GarlicWeapon.cs` | `GameServer.Component.Stage.Weapons` | 정상 |
| `Component/Stage/Weapons/KnifeWeapon.cs` | `GameServer.Component.Stage.Weapons` | 정상 |
| `Component/Stage/Weapons/AxeWeapon.cs` | `GameServer.Component.Stage.Weapons` | 정상 |

모든 파일의 namespace가 폴더 구조와 일치한다.

---

## 2. using 누락 여부 확인

### 2-1. WeaponBase.cs

```csharp
using GameServer.Component.Stage.Monster;
namespace GameServer.Component.Stage.Weapons;
```

`MonsterComponent`를 `Tick`, `TryAttack` 시그니처에서 사용한다. using 올바르게 추가됨.

### 2-2. GarlicWeapon.cs / KnifeWeapon.cs / AxeWeapon.cs

모두 `using GameServer.Component.Stage.Monster;` 추가됨. `TryAttack(IEnumerable<MonsterComponent>)` 파라미터에서 필요하다. 정상.

### 2-3. WaveSpawner.cs

```csharp
using GameServer.Component.Stage.Monster;
namespace GameServer.Component.Stage.Wave;
```

`MonsterType` enum 사용에 필요하다. using 올바르게 추가됨.

### 2-4. WeaponManager.cs

```csharp
using GameServer.Component.Player;
using GameServer.Component.Stage.Monster;
namespace GameServer.Component.Stage.Weapons;
```

`MonsterComponent`, `PlayerComponent`를 사용한다. 두 using 모두 존재. 정상.

### 2-5. GameStage.cs

```csharp
using GameServer.Component.Stage.Gem;
using GameServer.Component.Stage.Monster;
using GameServer.Component.Stage.Wave;
using GameServer.Component.Stage.Weapons;
```

4개의 하위 namespace using이 모두 선언되어 있다. `GemManager`, `MonsterComponent`, `MonsterType`, `WaveSpawner`, `WeaponManager`, `WeaponBase`, `WeaponId`, `WeaponHit`가 각각 올바른 using으로 해결된다. 누락 없음.

---

## 3. WeaponManager 리네임 참조 완전성

`WeaponSystem` / `_weaponSystem` 잔류 여부를 전체 GameServer 코드베이스에서 확인하였다.

**결과: 잔류 참조 없음.**

| 참조 위치 | 변경 전 | 변경 후 |
|-----------|---------|---------|
| `GameStage.cs` 필드 선언 | `WeaponSystem _weaponSystem` | `WeaponManager _weaponManager` |
| `GameStage.cs` Register 호출 | `_weaponSystem.Register(p)` | `_weaponManager.Register(p)` |
| `GameStage.cs` Tick 호출 | `_weaponSystem.Tick(...)` | `_weaponManager.Tick(...)` |
| `GameStage.cs` GetPrimaryWeaponId | `_weaponSystem.GetPrimaryWeaponId(...)` | `_weaponManager.GetPrimaryWeaponId(...)` |
| `GameStage.cs` ApplyChoice | `_weaponSystem.ApplyChoice(...)` | `_weaponManager.ApplyChoice(...)` |
| `GameStage.cs` GenerateChoices | `_weaponSystem.GenerateChoices(...)` | `_weaponManager.GenerateChoices(...)` |
| 파일명 | `WeaponSystem.cs` | `WeaponManager.cs` |
| 클래스 선언 | `public class WeaponSystem` | `public class WeaponManager` |

`RoomComponent.cs`, `GameSessionRegistry.cs`는 `GameServer.Component.Stage` namespace만 참조하며 `WeaponSystem`/`WeaponManager`를 직접 참조하지 않는다. 외부 노출 없이 `GameStage` 내부에 캡슐화되어 있어 리네임 영향 범위가 의도대로 제한되었다.

---

## 4. 아키텍처 평가

### 4-1. Namespace 분리 — 긍정적 평가

이번 정리로 Stage 하위 컴포넌트들이 명확하게 분리되었다.

- `Stage.Monster` — 몬스터 상태/AI
- `Stage.Gem` — 젬 수집
- `Stage.Wave` — 웨이브 스포너
- `Stage.Weapons` — 무기 시스템

각 서브시스템이 독립적인 namespace를 가지므로, 향후 파일이 많아지더라도 책임 경계가 명확하게 유지된다.

### 4-2. WeaponManager 클래스명 — 적절함

기존 `WeaponSystem`은 `GameServer.Systems/` 디렉토리의 전역 싱글턴 시스템들 (`DatabaseSystem`, `GameSessionRegistry` 등)과 동일한 접미사를 사용하여 위계상 혼란을 줄 수 있었다. `WeaponManager`로 변경함으로써 `GameStage` 내부의 인스턴스 단위 컴포넌트임을 명확하게 표현한다.

### 4-3. [관찰] WeaponManager — Unregister 호출 없음

```csharp
// WeaponManager.cs
public void Unregister(ulong accountId) => _playerWeapons.Remove(accountId);
```

`WeaponManager.Unregister`가 정의되어 있으나, `GameStage` 어디에서도 호출되지 않는다. 현재는 `GameStage` 수명 = 게임 1판 수명이고, 게임이 끝나면 `GameStage` 자체가 `Dispose`되므로 `_playerWeapons` Dictionary도 함께 해제된다. 따라서 메모리 누수는 없다.

다만 플레이어 이탈 처리 경로(`RoomComponent.Leave`)에서 게임 도중 이탈한 플레이어의 무기 데이터가 `_playerWeapons`에 계속 남아 `Tick`에서 해당 플레이어의 무기 틱이 계속 실행된다. `player.Character.IsAlive` 체크로 틱 처리는 건너뛰지만, 무기 쿨다운(`_elapsed`)은 계속 누적된다. 게임 내 이탈 후 재참여가 없는 현재 구조에서는 기능상 문제가 없다.

**권고:** 향후 게임 도중 이탈/재참여를 지원할 경우 `GameStage`에 `OnPlayerLeft` 훅을 추가하고 `_weaponManager.Unregister(player.AccountId)`를 호출하도록 정비할 것.

### 4-4. [관찰] GameStage — namespace 분리와 자체 namespace의 비대칭

`GameStage.cs`는 `namespace GameServer.Component.Stage`에 위치하며, 하위 컴포넌트들(`Monster`, `Gem`, `Wave`, `Weapons`)의 조립 지점 역할을 한다. 이 구조는 Facade 패턴에 가까우며, 하위 서브시스템이 자신의 namespace 안에 캡슐화되고 `GameStage`가 이를 using으로 가져오는 방식은 현재 규모에서 적절하다.

규모가 더 커진다면 `GameStage`를 별도의 `Stage/Core/` 또는 `Stage/Session/` 하위 namespace로 이동하는 것도 고려할 수 있으나, 현재 단계에서는 불필요하다.

---

## 5. 빌드 영향 검토

- `RoomComponent.cs`: `using GameServer.Component.Stage`로 `GameStage` 타입을 참조. namespace 변경 없이 `GameStage`는 여전히 `GameServer.Component.Stage`에 있으므로 영향 없음.
- `GameSessionRegistry.cs`: 동일 이유로 영향 없음.
- `WeaponBase`, `GarlicWeapon`, `KnifeWeapon`, `AxeWeapon`: `WeaponBase`에 `using ...Monster` 추가 후 하위 클래스에도 동일하게 추가. `WeaponBase`가 이미 해당 using을 포함하더라도 파일 단위 file-scoped namespace 구조에서는 하위 클래스 파일 각각에 using이 필요하다. 올바른 처리.

---

## 6. 종합 평가

| 항목 | 결과 | 비고 |
|------|------|------|
| Namespace — 폴더 경로 일치 | 정상 | 9개 파일 모두 일치 |
| using 누락 여부 | 없음 | 전체 의존 관계 충족 |
| WeaponManager 리네임 완전성 | 완전 | GameStage 내 6개 사용처 모두 반영, 외부 잔류 없음 |
| 아키텍처 일관성 | 향상 | System/Manager 명명 혼동 제거, 서브시스템 namespace 분리 완료 |

이번 정리는 기능 변경 없이 코드 구조만 개선하는 리팩토링으로, 모든 변경이 의도대로 반영되었다.

**추가 개선 권고 (선택, 우선순위 낮음)**

1. `WeaponManager.Unregister` — 게임 도중 플레이어 이탈 시 호출 경로 연결. 현재 기능상 무해하나 방어적 정리 차원에서 권고.
2. 이전 리뷰(2차)에서 지적한 `SendWeaponChoices` 및 기타 fire-and-forget `SendAsync`에 `ContinueWith(OnlyOnFaulted)` 패턴 일관 적용 — 이번 세션 범위 외이므로 별도 태스크 처리.
