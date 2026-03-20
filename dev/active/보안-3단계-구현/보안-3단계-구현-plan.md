# 보안 3단계 구현 계획

Last Updated: 2026-03-20

---

## Executive Summary

게임 서버 보안을 3단계로 순차 강화한다.
- **1차**: 네트워크 패킷 자체를 AES-GCM으로 암호화
- **2차**: 계정(username + password) 기반 회원가입/로그인으로 전환
- **3차**: DB에 저장되는 비밀번호를 BCrypt 해싱으로 보호

각 단계는 독립적으로 완성·배포 가능하며, 이전 단계가 완료되어야 다음 단계로 진행한다.

---

## Phase 1 — 패킷 암호화 (AES-128-GCM)

### 목표
클라이언트↔서버 간 모든 패킷을 암호화하여 평문 스니핑 방지.

### 알고리즘 선택: AES-128-GCM
| 항목 | 내용 |
|------|------|
| 알고리즘 | AES-128-GCM (인증 암호화 — 무결성 검증 내장) |
| 키 | 16바이트, `appsettings.json`에 Base64로 저장 |
| Nonce | 12바이트 (패킷마다 랜덤 생성, 암호문 앞에 prepend) |
| Auth Tag | 16바이트 (GCM 내장) |
| 키 방식 | Pre-shared key (서버·클라 동일 키 공유) |

### 패킷 포맷 변화
```
[Before]  [2B length] [protobuf bytes]
[After]   [2B length] [12B nonce] [encrypted(protobuf) + 16B auth-tag]
```

### 파이프라인 변경
```
현재:  framing-enc → framing-dec → protobuf-decoder → protobuf-encoder
변경:  framing-enc → [crypto] → framing-dec → [crypto] → protobuf-decoder → protobuf-encoder
```
- **Inbound**: `framing-dec` → **`EncryptionHandler(decrypt)`** → `protobuf-decoder`
- **Outbound**: `protobuf-encoder` → **`EncryptionHandler(encrypt)`** → `framing-enc`
- `EncryptionHandler` 위치: `framing-dec`와 `protobuf-decoder` 사이

### 봇 처리
- 봇도 동일한 AES 키를 사용 → 암호화 로직은 완전히 투명(transparent)
- `LoadTestConfig.EncryptionKey` (Base64) → 서버와 동일한 키

### 변경 파일

| 파일 | 변경 |
|------|------|
| `GameServer/appsettings.json` | `"Encryption": { "Key": "<base64 16B>" }` 추가 |
| `Common.Server/EncryptionSettings.cs` | 신규 — 설정 레코드 |
| `GameServer/Network/EncryptionHandler.cs` | 신규 — `MessageToMessageCodec<IByteBuffer, IByteBuffer>` |
| `GameServer/Network/GamePipelineInitializer.cs` | `EncryptionHandler` 파이프라인 삽입 |
| `GameClient/LoadTestConfig.cs` | `EncryptionKey` 필드 추가 |
| `GameClient/Program.cs` | 클라이언트 파이프라인에 `EncryptionHandler` 삽입 |

### Tasks
| # | 작업 | 수용 기준 | 노력 |
|---|------|----------|------|
| 1.1 | `EncryptionSettings` 레코드 + appsettings 등록 | IOptions 주입 가능 | S |
| 1.2 | `AesGcmCryptor` 유틸 (암호화/복호화 정적 메서드) | 단위 테스트 통과 | M |
| 1.3 | `EncryptionHandler` DotNetty 핸들러 구현 | encode/decode 양방향 | M |
| 1.4 | 서버 파이프라인 삽입 | 기존 테스트 클라 접속 가능 | S |
| 1.5 | 클라이언트 파이프라인 동기화 | 봇 1000개 부하 테스트 통과 | S |

---

## Phase 2 — 회원가입 및 비밀번호 인증

### 목표
`player_name` 기반 익명 로그인 → `username + password` 기반 계정 인증으로 전환.
비밀번호는 **4~16자** 제한.

### DB 변경
```sql
-- 신규 테이블
CREATE TABLE accounts (
    account_id    BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    username      VARCHAR(32)     NOT NULL,
    password_hash VARCHAR(128)    NOT NULL,   -- Phase 3에서 BCrypt로 교체
    created_at    DATETIME        NOT NULL,
    PRIMARY KEY (account_id),
    UNIQUE KEY ux_accounts_username (username)
);

-- players 테이블 변경
ALTER TABLE players ADD COLUMN account_id BIGINT UNSIGNED NULL;
```

### Proto 변경
```protobuf
// login.proto
message ReqLogin {
  string username = 1;   // player_name → username 으로 변경
  string password = 2;   // 신규 (4~16자)
}

// register.proto (신규)
message ReqRegister {
  string username = 1;   // 1~32자
  string password = 2;   // 4~16자
}
message ResRegister {
  uint64    account_id = 1;
  ErrorCode error_code = 2;
}

// error_codes.proto — 추가
INVALID_PASSWORD_LENGTH = 1002   // 비밀번호 길이 위반
USERNAME_TAKEN          = 1003   // 이미 존재하는 username
INVALID_CREDENTIALS     = 1004   // 비밀번호 불일치
```

### 봇 처리 방식: BotToken
- 봇은 `password` 필드에 **BotToken** 전송
- 서버: `password == BotToken` → 해당 username으로 계정 없으면 자동 생성 후 로그인
- `appsettings.json`에 `"Auth": { "BotToken": "bot-bypass-token" }` 추가
- `LoadTestConfig.BotToken` 필드 추가

### 로직 흐름

**회원가입 (`RegisterProcessor`)**
```
1. username 중복 체크 → USERNAME_TAKEN
2. password 길이 검증 (4~16) → INVALID_PASSWORD_LENGTH
3. accounts INSERT (password_hash = password 평문, Phase 3에서 교체)
4. ResRegister { account_id, Success }
```

**로그인 (`LoginProcessor` 수정)**
```
1. password == BotToken → 봇 계정 자동 생성/조회 후 통과
2. accounts SELECT WHERE username = ?
3. 계정 없음 or password_hash != password → INVALID_CREDENTIALS
4. (기존) 서버 정원, DB Insert, SessionSystem, Lobby 진입, ResLogin
```

### 변경 파일

| 파일 | 변경 |
|------|------|
| `db/schema_game.sql` | accounts 테이블, players.account_id 컬럼 추가 |
| `GameServer.Protocol/Protos/login.proto` | username/password 필드 추가 |
| `GameServer.Protocol/Protos/register.proto` | 신규 |
| `GameServer.Protocol/Protos/game_packet.proto` | ReqRegister/ResRegister oneof 추가 |
| `GameServer.Protocol/Protos/error_codes.proto` | 에러 코드 3개 추가 |
| `GameServer.Database/Rows/AccountRow.cs` | 신규 |
| `GameServer.Database/DbSets/AccountDbSet.cs` | 신규 |
| `GameServer.Database/GameDatabase.cs` | AccountDbSet 주입 |
| `Common.Server/AuthSettings.cs` | 신규 — BotToken 설정 |
| `GameServer/appsettings.json` | Auth 섹션 추가 |
| `GameServer/Network/RegisterProcessor.cs` | 신규 |
| `GameServer/Network/LoginProcessor.cs` | username/password 검증 로직 추가 |
| `GameServer/Network/GameServerHandler.cs` | ReqRegister → RegisterProcessor 라우팅 |
| `GameClient/LoadTestConfig.cs` | BotToken 필드 추가 |
| `GameClient/Controllers/ClientContext.cs` | Password 속성 추가 |
| `GameClient/Scenarios/BaseRoomScenario.cs` | ReqLogin.Password = ctx.Password |
| `GameClient/Program.cs` | ctx.Password = config.BotToken |

### Tasks
| # | 작업 | 수용 기준 | 노력 |
|---|------|----------|------|
| 2.1 | DB 스키마 변경 (accounts, players.account_id) | SQL 실행 성공 | S |
| 2.2 | AccountRow + AccountDbSet 구현 | SELECT/INSERT 가능 | S |
| 2.3 | Proto 변경 (register.proto, login.proto, error_codes.proto) | 빌드 성공 | S |
| 2.4 | `RegisterProcessor` 구현 | 중복 체크, 길이 검증, DB 저장 | M |
| 2.5 | `LoginProcessor` — 비밀번호 검증 + 봇 토큰 처리 삽입 | 잘못된 비번 거부, 봇 통과 | M |
| 2.6 | `GameServerHandler` — ReqRegister 라우팅 추가 | 등록 패킷 수신 처리 | S |
| 2.7 | 클라이언트 — BotToken 전송 | 봇 1000개 부하 테스트 통과 | S |

---

## Phase 3 — 비밀번호 BCrypt 해싱

### 목표
DB에 평문 비밀번호 저장 제거 → BCrypt 단방향 해시로 교체.

### 라이브러리
`BCrypt.Net-Next` (NuGet) — BCrypt 표준 구현, salt 자동 포함.

### 해시 스펙
| 항목 | 값 |
|------|-----|
| 알고리즘 | BCrypt |
| Work Factor | 11 (약 100ms/hash — 서버 부하와 보안 균형) |
| Salt | BCrypt 내장 (별도 저장 불필요) |
| 해시 길이 | 60자 → `accounts.password_hash VARCHAR(128)` 여유 충분 |

### BCrypt 흐름
```csharp
// 회원가입
string hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
// → "$2a$11$<22자 salt><31자 hash>" 형태로 DB 저장

// 로그인
bool valid = BCrypt.Net.BCrypt.Verify(password, storedHash);
```

### 봇 처리
- 봇 계정은 BotToken으로 자동 생성 → `password_hash`에 BCrypt hash of BotToken 저장
- 이후 재접속 시에도 `BCrypt.Verify(BotToken, hash)` 로 검증

### 변경 파일

| 파일 | 변경 |
|------|------|
| `GameServer/GameServer.csproj` | `BCrypt.Net-Next` NuGet 추가 |
| `GameServer/Network/RegisterProcessor.cs` | `BCrypt.HashPassword` 적용 |
| `GameServer/Network/LoginProcessor.cs` | `BCrypt.Verify` 적용 |

### Tasks
| # | 작업 | 수용 기준 | 노력 |
|---|------|----------|------|
| 3.1 | BCrypt.Net-Next NuGet 설치 | 빌드 성공 | S |
| 3.2 | `RegisterProcessor` — BCrypt 해싱 적용 | 신규 가입 시 hash 저장 | S |
| 3.3 | `LoginProcessor` — BCrypt.Verify 적용 | 평문 비교 제거, hash 검증 | S |
| 3.4 | 기존 계정 마이그레이션 | Phase 2와 함께 fresh start이므로 N/A | - |

---

## Risk Assessment

| 위험 | 영향 | 완화 |
|------|------|------|
| AES 키 유출 | 전체 트래픽 복호화 가능 | appsettings.json git-ignore, 환경변수 운영 권장 |
| Nonce 재사용 (GCM) | 암호 강도 저하 | 패킷마다 `RandomNumberGenerator` 사용 |
| BCrypt Work Factor 너무 높음 | 대량 봇 접속 시 CPU 폭증 | 봇은 캐시된 hash 재사용 (자동 처리됨) |
| proto 필드 순서 변경 | 기존 클라 호환 깨짐 | 기존 필드 번호 유지, 신규 필드만 추가 |

---

## 단계별 의존성

```
Phase 1 (패킷 암호화)
    └─ 독립 구현 가능

Phase 2 (회원가입/비밀번호)
    └─ Phase 1 완료 후 진행 권장 (암호화된 채널 위에서 비밀번호 전송)

Phase 3 (비밀번호 해싱)
    └─ Phase 2 완료 후 진행 (DB 스키마/로직 재사용)
```
