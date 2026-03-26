# 코드 품질 버그수정 — 작업 체크리스트
Last Updated: 2026-03-26

## HIGH 수정 (완료)
- ✅ H-1: `SessionSystem._running` → `volatile bool`
- ✅ H-2: `PlayerSystem.TryReserveLogin` TOCTOU 이중 검증
- ✅ H-3: `LobbyComponent._rooms` ConcurrentDictionary → Dictionary + lock 통일
- ✅ H-4: `SessionComponent` O(n) LINQ Count → `_typeCounters` O(1)
- ✅ H-5: BCrypt 도입 + `BCrypt.Verify` → `Task.Run` 래핑 (스레드풀 블로킹 방지)
- ✅ H-6: `GameStage.RunTickAsync` ObjectDisposedException catch
- ✅ H-7: `WeaponManager.Clear()` + `GameStage.Dispose()`에서 `_stateLock` 하에 호출
- ✅ 빌드 경고 0, 오류 0

## MEDIUM 수정 (미착수)
- ⬜ M-1: Singleton 6개 DI 전환 (`싱글톤-DI-리팩토링` 작업과 병합 검토)
- ⬜ M-2: `PlayerComponent` SRP 위반 — 패킷라우팅/DB/세션/생명주기 분리
- ⬜ M-3: `GameStage` 570줄 분리 — CombatSystem, BroadcastHelper 추출
- ⬜ M-4: `LobbyComponent` RoomFactory 도입
- ⬜ M-5: `RoomComponent` DB 직접 호출 → 이벤트/콜백 위임
- ⬜ M-6: `LoginProcessor`/`RegisterProcessor` → `Services/` 폴더로 이동
- ⬜ M-7: `ShutdownSystem._cts` Initialize 전 호출 가드
- ⬜ M-8: `GemManager._gemIdSeq` static 의도 주석 명시
- ⬜ M-9: `GameStage` BaseComponent 상속 검토

## LOW 수정 (미착수)
- ⬜ L-1~L-8: 네이밍/스타일 정리 (별도 정리 세션에서 일괄 처리 권장)

## 커밋 대기 중
- 이 세션 수정 파일 9개 + 이전 세션(네이밍 정리) 파일 9개 = 총 약 18개 파일 미커밋
