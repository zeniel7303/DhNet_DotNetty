# 보안 3단계 구현 — 컨텍스트

Last Updated: 2026-03-20

---

## 현재 구현 상태

| Phase | 상태 |
|-------|------|
| Phase 1 (AES-128-GCM 패킷 암호화) | ✅ 완료, 커밋 대기 중 |
| Phase 2 (회원가입/비밀번호 4~16자) | ✅ 구현 완료 + Opus 코드 리뷰 완료, 커밋 대기 중 |
| Phase 3 (BCrypt 해싱) | 미착수 |

---

## Phase 2 — 최종 수정된 파일 목록

> ⚠️ 아래는 이번 세션에서 확정된 최종 상태. BotToken 방식으로 1차 구현 후 폐기하고 ReqRegister→ReqLogin 방식으로 재설계.

| 파일 | 변경 유형 | 내용 |
|------|----------|------|
| `db/schema_game.sql` | 수정 | accounts 테이블 추가, players.account_id 컬럼 추가 |
| `GameServer.Database/Rows/AccountRow.cs` | 신규 | accounts 테이블 POCO |
| `GameServer.Database/Rows/PlayerRow.cs` | 수정 | account_id 필드 추가 |
| `GameServer.Database/DbSet/AccountDbSet.cs` | 신규 | InsertAsync, SelectByUsernameAsync |
| `GameServer.Database/DbSet/PlayerDbSet.cs` | 수정 | InsertAsync SQL에 account_id 컬럼 추가 |
| `GameServer.Database/System/GameDbContext.cs` | 수정 | Accounts DbSet 주입 |
| `GameServer.Protocol/Protos/error_codes.proto` | 수정 | 에러코드 4개 추가 (1002~1005) |
| `GameServer.Protocol/Protos/login.proto` | 수정 | ReqLogin에 password 필드 추가 |
| `GameServer.Protocol/Protos/register.proto` | 신규 | ReqRegister/ResRegister 메시지 |
| `GameServer.Protocol/Protos/game_packet.proto` | 수정 | register import + oneof 18/19 추가, error_codes import 제거 |
| `Common.Server/AuthSettings.cs` | **삭제** | BotToken 방식 폐기로 파일 자체 삭제 |
| `GameServer/appsettings.json` | 수정 | Auth 섹션 완전 제거 (BotToken 삭제) |
| `GameServer/Network/RegisterProcessor.cs` | 신규 | 회원가입 처리 (username 4~16자, INSERT IGNORE) |
| `GameServer/Network/LoginProcessor.cs` | 수정 | AuthenticateAsync 추가, player.Name = account.username |
| `GameServer/Network/SessionComponent.cs` | 수정 | TrySetRegisterStarted() CAS 플래그 추가 |
| `GameServer/Network/GameServerHandler.cs` | 수정 | if-else → switch-case, ReqRegister 케이스 추가 |
| `GameServer/ServerStartup.cs` | 수정 | AuthSettings/LoginProcessor.Initialize() 라인 제거 |
| `GameClient/LoadTestConfig.cs` | 수정 | BotToken 필드 **제거** |
| `GameClient/Controllers/ClientContext.cs` | 수정 | Password 속성 추가 (기본값 "0000") |
| `GameClient/Program.cs` | 수정 | ctx.Password = config.BotToken 라인 **제거** |
| `GameClient/Scenarios/BaseRoomScenario.cs` | 수정 | OnConnected → ReqRegister, ResRegister SUCCESS/USERNAME_TAKEN → ReqLogin |
| `GameClient/Scenarios/LobbyChatScenario.cs` | 수정 | 동일 패턴 |
| `GameClient/Scenarios/ReconnectStressScenario.cs` | 수정 | 동일 패턴 + 에러 처리 |

---

## 주요 아키텍처 결정사항

### 1. BotToken 방식 폐기 → ReqRegister→ReqLogin 플로우

**최초 구현 (폐기)**: 봇 전용 BotToken을 ReqLogin에 special_password로 넣어 서버가 자동 계정 생성
**최종 결정**: 봇도 일반 사용자와 동일한 ReqRegister→ReqLogin 흐름 사용

```
클라이언트 OnConnected:
  1. ReqRegister { username, password } 전송
  2. ResRegister 수신:
     - SUCCESS 또는 USERNAME_TAKEN → ReqLogin 전송 (USERNAME_TAKEN = 기존 계정 존재)
     - 기타 에러 → 연결 종료
  3. ResLogin 수신 → 게임 진행
```

봇 비밀번호는 "0000"으로 고정 (`ClientContext.Password` 기본값).

**이유**: BotToken은 서버 로직을 복잡하게 만들고 봇/사용자 경로를 이원화함. ReqRegister 자체로 중복 처리(INSERT IGNORE + USERNAME_TAKEN)가 되므로 별도 특수 처리 불필요.

### 2. 플레이어 이름 = account.username (DB 값 우선)

```csharp
// LoginProcessor.cs
var player = new PlayerComponent(session, account.username);
// req.PlayerName은 username 조회에만 사용, player 이름으로는 무시
```

클라이언트가 임의 이름을 보내도 DB의 username이 사용됨. 이름 조작 불가.

### 3. 중복 로그인 처리 — 기존 세션 종료 (옵션 C)

```csharp
// LoginProcessor.cs
if (session.Player != null)
{
    GameLogger.Warn("Login", $"이미 로그인된 세션에서 ReqLogin 수신 — 연결 종료");
    await session.Channel.CloseAsync();
    return;
}
```

이미 로그인된 세션에서 ReqLogin 재수신 시 연결 종료. 중복 세션 방지.

### 4. Username/Password 길이 검증 (서버 측)

- Username: 4~16자 (Trim 후 기준)
- Password: 4~16자 (Trim 없음, 공백도 비밀번호의 일부)
- 검증 실패 → INVALID_USERNAME_LENGTH / INVALID_PASSWORD_LENGTH 응답

### 5. User Enumeration 방지

```csharp
if (account == null || account.password_hash != password)
    ResLogin = new ResLogin { ErrorCode = ErrorCode.InvalidCredentials }
```

username 없음과 password 불일치를 동일 에러로 응답. 공격자가 유효 username 식별 불가.

### 6. game_packet.proto error_codes import 정리

`error_codes.proto`를 `game_packet.proto`에서 import 제거. `ErrorCode`는 각 메시지별 proto에서 직접 import. proto 컴파일러 unused import 경고 제거.

### 7. account_id → players 테이블 nullable

기존 데이터 호환성 위해 `BIGINT UNSIGNED NULL`. 신규 로그인 시 항상 값 설정.

---

## Phase 2 에러 코드 (확정)

| 코드 | 값 | 상황 |
|------|----|------|
| `INVALID_USERNAME_LENGTH` | 1002 | username 4~16자 위반 |
| `INVALID_PASSWORD_LENGTH` | 1003 | 비밀번호 4~16자 위반 |
| `USERNAME_TAKEN`          | 1004 | 이미 사용 중인 username |
| `INVALID_CREDENTIALS`     | 1005 | username 없거나 password 불일치 |

> ⚠️ 에러 코드 번호가 세션 중 재조정됨 (INVALID_USERNAME_LENGTH 추가로 기존 1002→1003→1004→1005로 시프트)

---

## Opus 코드 리뷰 결과 요약 (2026-03-20)

결과 파일: `dev/active/패킷-암호화-AES-GCM/패킷-암호화-AES-GCM-code-review.md` (Phase 2 섹션)

| 심각도 | 항목 | 파일 |
|--------|------|------|
| Critical | 평문 비밀번호 저장 (Phase 3에서 해결 예정, 의도적) | RegisterProcessor.cs:81 |
| Minor | INSERT 성공 후 SELECT null 방어 코드 누락 (`created!`) | RegisterProcessor.cs:121 |
| Minor | 로그에 username 원문 기록 (Log Injection 가능성) | RegisterProcessor.cs:25 |
| Minor | `login.proto` player_name 필드명이 실제로는 username | login.proto:8 |
| Minor | LobbyChatScenario Register/Login 로직 중복 | LobbyChatScenario.cs |

Major 이슈 없음. 핵심 흐름, 동시성, 에러 처리 모두 올바름.

**Minor M1 (created! null) 처리 여부**: 아직 미수정. Phase 3 작업 전 수정 권장.

---

## SRP vs BCrypt 논의 (2026-03-20)

사용자가 "서버도 비밀번호를 몰라야 하지 않나?"라고 문의. SRP(Secure Remote Password) 검토.

**결론**: 게임 서버에서는 BCrypt + AES-GCM이 충분. SRP 미도입 결정.
- SRP: 2-RTT(패킷 4번), 불안정한 C# 라이브러리, 프로토콜 전면 변경 필요
- BCrypt: 전송은 AES-GCM으로 보호, DB에는 해시만 저장, 업계 표준
- AES-GCM 전송 구간에서 서버가 평문을 잠깐 보는 것은 허용 가능한 트레이드오프

---

## Phase 3 BCrypt 변경 범위

```csharp
// 1. GameServer.csproj — BCrypt.Net-Next NuGet 추가

// 2. RegisterProcessor.cs:81
password_hash = password
// → BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11)

// 3. LoginProcessor.cs:167 (AuthenticateAsync)
account.password_hash != password
// → !BCrypt.Net.BCrypt.Verify(password, account.password_hash)

// 4. 주의: Timing Attack 방어
// username 미존재 시에도 dummy hash와 BCrypt.Verify 수행 (일정 시간 소요로 User Enumeration 방지)
```

DB 스키마, AccountRow 변경 불필요. 변경 지점 2곳으로 최소화됨.

---

## 커밋 대기 중인 변경사항

Phase 1 + Phase 2 변경사항이 미커밋 상태. 사용자가 직접 커밋/푸시.

---

## 다음 단계: Phase 3 (BCrypt 해싱)

1. `GameServer.csproj` — `BCrypt.Net-Next` NuGet 패키지 추가
2. (선택) Minor M1 수정: `RegisterProcessor.cs:121` created null 방어 추가
3. `RegisterProcessor.cs` — BCrypt.HashPassword 적용
4. `LoginProcessor.cs` — BCrypt.Verify 적용 + Timing Attack 방어 (dummy hash)
5. 검증: 올바른 비번 → 로그인 성공, 틀린 비번 → INVALID_CREDENTIALS

---

## 주요 파일 경로 참조

| 항목 | 경로 |
|------|------|
| 서버 파이프라인 | `GameServer/Network/GamePipelineInitializer.cs` |
| 로그인 처리 | `GameServer/Network/LoginProcessor.cs` |
| 회원가입 처리 | `GameServer/Network/RegisterProcessor.cs` |
| 서버 핸들러 | `GameServer/Network/GameServerHandler.cs` |
| 세션 컴포넌트 | `GameServer/Network/SessionComponent.cs` |
| DB 스키마 | `db/schema_game.sql` |
| 계정 DB셋 | `GameServer.Database/DbSet/AccountDbSet.cs` |
| 서버 설정 | `GameServer/appsettings.json` |
| 부하 테스트 설정 | `GameClient/LoadTestConfig.cs` |
| 클라 컨텍스트 | `GameClient/Controllers/ClientContext.cs` |
| 코드 리뷰 결과 | `dev/active/패킷-암호화-AES-GCM/패킷-암호화-AES-GCM-code-review.md` |
