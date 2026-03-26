# 코드 품질 버그수정 — 컨텍스트
Last Updated: 2026-03-26

## 배경

Opus 모델 전체 코드 리뷰 결과, HIGH(잠재적 버그/크래시) 7건을 이번 세션에서 전부 수정.
MEDIUM 9건, LOW 8건은 미수정 상태 (별도 정리 필요).

---

## 이번 세션 수정 내역 (HIGH 7건)

### H-1. `SessionSystem._running` — `volatile` 추가
- **파일**: `GameServer/Systems/SessionSystem.cs:43`
- **문제**: `_running` 필드가 메인 스레드에서 쓰이고 백그라운드 `Loop()` 스레드에서 읽히는데, `volatile` 없어 JIT 최적화로 변경이 전파 안 될 수 있음
- **수정**: `private bool _running` → `private volatile bool _running`

### H-2. `PlayerSystem.TryReserveLogin` — TOCTOU 이중 검증
- **파일**: `GameServer/Systems/PlayerSystem.cs:27`
- **문제**: `ContainsKey(_players)` → `TryAdd(_reservedAccounts)` 사이에 다른 스레드가 `_players.TryAdd`를 실행하면 이미 활성 플레이어인데 예약이 성공
- **수정**: `TryAdd` 성공 후 `_players.ContainsKey` 재확인. 있으면 `_reservedAccounts.TryRemove` 후 false 반환

### H-3. `LobbyComponent._rooms` — 동기화 메커니즘 통일
- **파일**: `GameServer/Component/Lobby/LobbyComponent.cs:25`
- **문제**: `ConcurrentDictionary` + `lock(_roomLock)` 혼용 — 읽기 경로는 lock 없이 ConcurrentDict, 쓰기는 lock. 두 메커니즘 혼재로 의도 불명확
- **수정**: `ConcurrentDictionary` → `Dictionary`, 읽기(`GetRoomList`, `TryGetRoom`, `GetRooms`, `RoomCount`) 포함 모든 접근을 `_roomLock`으로 통일
- **주의**: `TryRemove` → `Remove(key, out value)` 로 변경 (Dictionary API 차이)

### H-4. `SessionComponent` — O(n) LINQ Count → O(1) 카운터
- **파일**: `GameServer/Network/SessionComponent.cs`
- **문제**: `PacketPairPolicy`에 전달되는 `_getQueueCount` delegate가 `_packetQueue.Count(p => p.PayloadCase == t)` — 매 패킷 수신마다 큐 전체 순회 O(n)
- **수정**:
  - `_typeCounters: ConcurrentDictionary<PayloadOneofCase, int>` 추가
  - `ProcessPacket`: 큐 Enqueue 후 `_typeCounters.AddOrUpdate(type, 1, ...)` 증가
  - `DrainPackets`: Dequeue 후 `_typeCounters.AddOrUpdate(type, 0, ...-1)` 감소
  - `ClearPacketQueue`: 전체 드레인 후 `_typeCounters.Clear()`
  - delegate: `t => _typeCounters.GetValueOrDefault(t, 0)` (O(1))

### H-5. `LoginProcessor` / `RegisterProcessor` — BCrypt 도입
- **파일**: `GameServer/Network/LoginProcessor.cs:241`, `RegisterProcessor.cs:63`, `GameServer.csproj`
- **문제**: 비밀번호 평문 비교/저장 (DB 컬럼명은 이미 `password_hash`였으나 실제 평문 저장)
- **수정**:
  - `GameServer.csproj`에 `BCrypt.Net-Next 4.0.3` 패키지 추가
  - `RegisterProcessor`: `password_hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11)`
  - `LoginProcessor`: `!BCrypt.Net.BCrypt.Verify(password, account.password_hash)`
- **⚠️ 브레이킹 체인지**: 기존 DB에 평문 저장된 계정 로그인 불가 → 테스트 계정 재가입 필요

### H-6. `GameStage.RunTickAsync` — `ObjectDisposedException` 처리
- **파일**: `GameServer/Component/Stage/GameStage.cs:144`
- **문제**: `Dispose()`에서 `_cts.Cancel()` 직후 `_cts.Dispose()` 호출 — `WaitForNextTickAsync`가 Dispose된 CTS를 접근하면 `ObjectDisposedException` 발생, 기존 catch에 미포함
- **수정**: `catch (ObjectDisposedException) { }` 추가

### H-7. `WeaponManager.Clear()` 미구현 + `GameStage.Dispose()` 호출 누락
- **파일**: `GameServer/Component/Stage/Weapons/WeaponManager.cs:30`, `GameStage.cs:563`
- **문제**: `Unregister()`가 정의는 있으나 호출 없어 `_playerWeapons` 데이터 잔류
- **수정**:
  - `WeaponManager.Clear()` 메서드 추가: `_playerWeapons.Clear()`
  - `GameStage.Dispose()`에서 `_weaponManager.Clear()` 호출

---

## 수정된 파일 목록

| 파일 | 수정 내용 |
|------|----------|
| `Systems/SessionSystem.cs` | `_running` volatile |
| `Systems/PlayerSystem.cs` | `TryReserveLogin` 이중 검증 |
| `Component/Lobby/LobbyComponent.cs` | `_rooms` Dictionary + lock 통일 |
| `Network/SessionComponent.cs` | `_typeCounters` 추가, O(1) Count |
| `Network/LoginProcessor.cs` | BCrypt.Verify |
| `Network/RegisterProcessor.cs` | BCrypt.HashPassword |
| `GameServer.csproj` | BCrypt.Net-Next 4.0.3 패키지 |
| `Component/Stage/GameStage.cs` | ObjectDisposedException catch, _weaponManager.Clear() |
| `Component/Stage/Weapons/WeaponManager.cs` | Clear() 메서드 추가 |

---

## 미수정 항목 (MEDIUM/LOW — 별도 작업 필요)

### MEDIUM (우선순위 높은 순)
- **M-1**: Singleton 6개 (`SessionSystem`, `PlayerSystem` 등) — DI로 전환 (싱글톤-DI-리팩토링 작업과 연결)
- **M-2**: `PlayerComponent` SRP 위반 — 패킷라우팅/DB/세션/생명주기 분리
- **M-3**: `GameStage` 570줄 과다 — CombatSystem 등으로 분리
- **M-4**: `LobbyComponent`가 `new RoomComponent()` 직접 생성 — RoomFactory 도입
- **M-5**: `RoomComponent`가 DB 직접 호출 — 레이어 역전
- **M-6**: `LoginProcessor`/`RegisterProcessor`가 `Network/` 폴더에 위치 — Services로 이동
- **M-7**: `ShutdownSystem._cts` Initialize 전 Request 호출 시 무시됨
- **M-8**: `GemManager._gemIdSeq` static 의도 불명확 (현재는 글로벌 유니크 보장)
- **M-9**: `GameStage`가 `BaseComponent` 미상속

### LOW (스타일 정리)
- L-1: `GameStage.Start()` → `StartAsync()` 이름 고려
- L-2: `WebSocketFrameHandler.WriteAsync` flush 의도 미주석
- L-3: `PacketPolicyResult` record 단순화
- L-4: `PlayerComponent.OnDispose` null-forgiving operator
- L-5: `AppConfig` 접근 제한자 누락
- L-6: `GameServer.Controllers` vs `GameServer.PacketHandlers` 네이밍
- L-7: `RoomComponent.MaxPlayers` 하드코딩
- L-8: `CollectGems` 브로드캐스트/개인 전송 패턴 혼재

---

## 현재 상태

- 빌드: 경고 0, 오류 0
- 커밋 미완료 (이번 세션 수정 전체 미커밋)
- 이전 세션(네이밍 정리) + 이번 세션(버그수정) 합산 약 20개 파일 변경

## 다음 단계

1. MEDIUM 항목 중 우선순위 선택하여 순차 수정
2. 또는 커밋 먼저 수행 후 다음 세션에서 MEDIUM 시작
