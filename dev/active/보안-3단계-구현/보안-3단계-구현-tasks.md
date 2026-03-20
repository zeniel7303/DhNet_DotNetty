# 보안 3단계 구현 — 작업 체크리스트

Last Updated: 2026-03-20

---

## Phase 1 — 패킷 암호화 (AES-128-GCM) ✅ 구현 완료

- ✅ **1.1** `Common.Server/EncryptionSettings.cs` 생성
- ✅ **1.2** `GameServer/appsettings.json` — Encryption 섹션 추가 (Key: "" 비활성화)
- ✅ **1.3** `Common.Shared/Crypto/AesGcmCryptor.cs` 구현
- ✅ **1.4** `GameServer/Network/AesGcmDecryptionHandler.cs` + `AesGcmEncryptionHandler.cs`
- ✅ **1.5** `GameServer/Network/GamePipelineInitializer.cs` — crypto 핸들러 삽입
- [ ] **1.6** `GameClient/LoadTestConfig.cs` — `EncryptionKey` 필드 추가
- ✅ **1.6** `GameClient/Network/AesGcmDecryptionHandler.cs` + `AesGcmEncryptionHandler.cs`
- ✅ **1.7** `GameClient/LoadTestConfig.cs` — EncryptionKey + --encryption-key CLI
- ✅ **1.8** `GameClient/Program.cs` — 두 파이프라인 모두 적용 (일반 + reconnect-stress)
- ✅ **1.9** 빌드 성공 (경고 0, 오류 0)
- ✅ **1.10** 부하 테스트 통과 (암호화 ON/OFF 비교, 1000 클라이언트, 에러 0, 오버헤드 ~12%)

## Phase 2 — 회원가입 및 비밀번호 (4~16자) ✅ 구현 완료 + 코드 리뷰 완료

> ⚠️ BotToken 방식으로 1차 구현 후 폐기 → ReqRegister→ReqLogin 방식으로 재설계 완료

### DB / 데이터 레이어
- ✅ **2.1** `db/schema_game.sql` — accounts 테이블 추가
- ✅ **2.2** `db/schema_game.sql` — players.account_id 컬럼 추가 (nullable)
- ✅ **2.3** `GameServer.Database/Rows/AccountRow.cs` 생성
- ✅ **2.4** `GameServer.Database/DbSet/AccountDbSet.cs` 생성 (InsertAsync, SelectByUsernameAsync)
- ✅ **2.5** `GameServer.Database/System/GameDbContext.cs` — AccountDbSet 주입
- ✅ **2.5b** `GameServer.Database/Rows/PlayerRow.cs` — account_id 필드 추가
- ✅ **2.5c** `GameServer.Database/DbSet/PlayerDbSet.cs` — InsertAsync SQL account_id 추가

### Proto
- ✅ **2.6** `GameServer.Protocol/Protos/login.proto` — `password = 2` 필드 추가
- ✅ **2.7** `GameServer.Protocol/Protos/register.proto` 신규 생성
- ✅ **2.8** `GameServer.Protocol/Protos/game_packet.proto` — register import + oneof 18/19 추가, error_codes unused import 제거
- ✅ **2.9** `GameServer.Protocol/Protos/error_codes.proto` 에러 코드 추가 (4개, 1002~1005)
  - `INVALID_USERNAME_LENGTH=1002`, `INVALID_PASSWORD_LENGTH=1003`, `USERNAME_TAKEN=1004`, `INVALID_CREDENTIALS=1005`

### 서버 로직
- ✅ **2.10** `Common.Server/AuthSettings.cs` — BotToken 방식 폐기로 **파일 삭제**
- ✅ **2.11** `GameServer/appsettings.json` — Auth 섹션 완전 **제거**
- ✅ **2.12** `GameServer/Network/RegisterProcessor.cs` 신규 구현
  - username/password 4~16자 검증, INSERT IGNORE, rows_affected 이중 체크
- ✅ **2.13** `GameServer/Network/LoginProcessor.cs` — AuthenticateAsync 추가
  - player.Name = account.username (DB 값 우선, req.PlayerName 무시)
  - 중복 로그인 시 CloseAsync() (옵션 C)
- ✅ **2.14** `GameServer/Network/GameServerHandler.cs` — if-else → switch-case, ReqRegister 추가
- ✅ **2.14b** `GameServer/Network/SessionComponent.cs` — TrySetRegisterStarted() CAS 추가
- ✅ **2.14c** `GameServer/ServerStartup.cs` — AuthSettings/LoginProcessor.Initialize() 라인 **제거**

### 클라이언트
- ✅ **2.15** `GameClient/LoadTestConfig.cs` — BotToken 필드 **제거**
- ✅ **2.16** `GameClient/Controllers/ClientContext.cs` — Password 속성 추가 (기본값 "0000")
- ✅ **2.17** `GameClient/Program.cs` — ctx.Password = config.BotToken 라인 **제거** (기본값 사용)
- ✅ **2.18** `GameClient/Scenarios/BaseRoomScenario.cs` — OnConnected → ReqRegister, ResRegister → ReqLogin
- ✅ **2.18b** `GameClient/Scenarios/LobbyChatScenario.cs` — 동일 패턴
- ✅ **2.18c** `GameClient/Scenarios/ReconnectStressScenario.cs` — 동일 패턴 + 에러 처리
- ✅ **2.19-빌드** 빌드 성공 (경고 0, 오류 0)
- [ ] **2.19-검증** DB 적용 후 실제 테스트: 잘못된 비번 → INVALID_CREDENTIALS, 봇 → 로그인 성공

### 코드 리뷰 (Opus, 2026-03-20)
- ✅ **2.20** Opus 모델로 코드 리뷰 완료
  - Critical 1건 (평문 저장 — Phase 3에서 해결 예정, 의도적)
  - Major 0건
  - Minor 4건 (M1: created! null 방어 누락, M2: 로그 Log Injection, M3: proto 필드명, M4: 코드 중복)
- [ ] **2.21** Minor M1 수정: `RegisterProcessor.cs:121` `created!` → null 체크 추가 (Phase 3 전 권장)

## Phase 3 — 비밀번호 BCrypt 해싱

- [ ] **3.1** `GameServer/GameServer.csproj` — `BCrypt.Net-Next` NuGet 패키지 추가
- [ ] **3.2** `GameServer/Network/RegisterProcessor.cs` — `BCrypt.HashPassword(password, 11)` 적용
- [ ] **3.3** `GameServer/Network/LoginProcessor.cs` — `BCrypt.Verify(password, hash)` 적용
  - username 미존재 시에도 dummy BCrypt.Verify로 Timing Attack 방어 필요
- [ ] **3.4** 기존 계정 마이그레이션 SQL (평문 → BCrypt 해시 일괄 변환)
- [ ] **3.5** 검증: 올바른 비번 → 로그인 성공, 틀린 비번 → INVALID_CREDENTIALS

---

## 진행 상태

| Phase | 상태 |
|-------|------|
| Phase 1 (패킷 암호화) | ✅ 완료 (커밋 대기) |
| Phase 2 (회원가입/비밀번호) | ✅ 구현 완료 + 코드 리뷰 완료 (커밋 대기, DB 검증 필요) |
| Phase 3 (BCrypt 해싱) | 미착수 (Phase 2 커밋 + DB 검증 후 시작) |
