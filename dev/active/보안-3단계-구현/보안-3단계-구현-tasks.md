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
- [ ] **1.10** 실 키 설정 후 봇 부하 테스트 수동 검증

## Phase 2 — 회원가입 및 비밀번호 (4~16자)

### DB / 데이터 레이어
- [ ] **2.1** `db/schema_game.sql` — accounts 테이블 추가
- [ ] **2.2** `db/schema_game.sql` — players.account_id 컬럼 추가
- [ ] **2.3** `GameServer.Database/Rows/AccountRow.cs` 생성
- [ ] **2.4** `GameServer.Database/DbSets/AccountDbSet.cs` 생성
  - `InsertAsync`, `SelectByUsernameAsync` 메서드
- [ ] **2.5** `GameServer.Database/GameDatabase.cs` — AccountDbSet 주입

### Proto
- [ ] **2.6** `GameServer.Protocol/Protos/login.proto` — `username`, `password` 필드 추가
- [ ] **2.7** `GameServer.Protocol/Protos/register.proto` 신규 생성
  - `ReqRegister { username, password }`, `ResRegister { account_id, error_code }`
- [ ] **2.8** `GameServer.Protocol/Protos/game_packet.proto` — ReqRegister/ResRegister oneof 추가
- [ ] **2.9** `GameServer.Protocol/Protos/error_codes.proto` 에러 코드 추가
  - `INVALID_PASSWORD_LENGTH = 1002`
  - `USERNAME_TAKEN = 1003`
  - `INVALID_CREDENTIALS = 1004`

### 서버 로직
- [ ] **2.10** `Common.Server/AuthSettings.cs` — BotToken 설정 레코드
- [ ] **2.11** `GameServer/appsettings.json` — `"Auth": { "BotToken": "..." }` 추가
- [ ] **2.12** `GameServer/Network/RegisterProcessor.cs` 신규 구현
  - username 중복 체크 → USERNAME_TAKEN
  - password 길이 검증 4~16자 → INVALID_PASSWORD_LENGTH
  - accounts INSERT (password_hash = 평문, Phase 3에서 교체)
- [ ] **2.13** `GameServer/Network/LoginProcessor.cs` — 계정 검증 로직 삽입 (최상단)
  - BotToken 체크 → 봇 자동 계정 생성/조회
  - accounts SELECT → INVALID_CREDENTIALS
- [ ] **2.14** `GameServer/Network/GameServerHandler.cs` — ReqRegister 라우팅 추가

### 클라이언트
- [ ] **2.15** `GameClient/LoadTestConfig.cs` — `BotToken` 필드 추가
- [ ] **2.16** `GameClient/Controllers/ClientContext.cs` — `Password` 속성 추가
- [ ] **2.17** `GameClient/Program.cs` — `ctx.Password = config.BotToken` 초기화
- [ ] **2.18** `GameClient/Scenarios/BaseRoomScenario.cs` — `ReqLogin.Password = ctx.Password` 전송
- [ ] **2.19** 검증: 잘못된 비번 → INVALID_CREDENTIALS, 봇 → 로그인 성공

## Phase 3 — 비밀번호 BCrypt 해싱

- [ ] **3.1** `GameServer/GameServer.csproj` — `BCrypt.Net-Next` NuGet 패키지 추가
- [ ] **3.2** `GameServer/Network/RegisterProcessor.cs` — `BCrypt.HashPassword(password, 11)` 적용
- [ ] **3.3** `GameServer/Network/LoginProcessor.cs` — `BCrypt.Verify(password, hash)` 적용
  - 봇 자동 계정 생성 시에도 `BCrypt.HashPassword(BotToken)` 저장
- [ ] **3.4** 검증: 올바른 비번 → 로그인 성공, 틀린 비번 → INVALID_CREDENTIALS

---

## 진행 상태

| Phase | 상태 |
|-------|------|
| Phase 1 (패킷 암호화) | 계획 완료, 미착수 |
| Phase 2 (회원가입/비밀번호) | 계획 완료, Phase 1 완료 후 시작 |
| Phase 3 (BCrypt 해싱) | 계획 완료, Phase 2 완료 후 시작 |
