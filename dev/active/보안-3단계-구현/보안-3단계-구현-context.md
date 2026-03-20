# 보안 3단계 구현 — 컨텍스트

Last Updated: 2026-03-20

---

## 현재 파이프라인 (GamePipelineInitializer.cs:14-20)

```csharp
pipeline.AddLast("framing-enc", new LengthFieldPrepender(2));
pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(ushort.MaxValue, 0, 2, 0, 2));
pipeline.AddLast("protobuf-decoder", new ProtobufDecoder(GamePacket.Parser));
pipeline.AddLast("protobuf-encoder", new ProtobufEncoder());
pipeline.AddLast("idle", new IdleStateHandler(readerIdleTimeSeconds: 30, ...));
pipeline.AddLast("heartbeat", HeartbeatHandler.Instance);
pipeline.AddLast("handler", new GameServerHandler());
```

## Phase 1 후 파이프라인

```csharp
pipeline.AddLast("framing-enc",    new LengthFieldPrepender(2));
pipeline.AddLast("framing-dec",    new LengthFieldBasedFrameDecoder(...));
pipeline.AddLast("crypto",         new EncryptionHandler(key));  // ← 삽입 위치
pipeline.AddLast("protobuf-decoder", ...);
pipeline.AddLast("protobuf-encoder", ...);
// idle, heartbeat, handler 유지
```

### EncryptionHandler 패킷 구조

```
Wire:  [2B length] [12B nonce] [N bytes encrypted] [16B auth-tag]
                  └──────────────────────────────────────────────┘
                               EncryptionHandler 담당 범위
```

### AesGcmCryptor 핵심 시그니처

```csharp
internal static class AesGcmCryptor
{
    // 암호화: plaintext → [nonce(12B) | ciphertext | tag(16B)]
    static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext);

    // 복호화: [nonce(12B) | ciphertext | tag(16B)] → plaintext
    static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> ciphertext);
}
```

---

## Phase 2 DB 스키마

```sql
-- 신규
CREATE TABLE accounts (
    account_id    BIGINT UNSIGNED  NOT NULL AUTO_INCREMENT,
    username      VARCHAR(32)      NOT NULL,
    password_hash VARCHAR(128)     NOT NULL,
    created_at    DATETIME         NOT NULL,
    PRIMARY KEY (account_id),
    UNIQUE KEY ux_accounts_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 기존 변경
ALTER TABLE players
    ADD COLUMN account_id BIGINT UNSIGNED NULL AFTER player_id;
```

## Phase 2 LoginProcessor 수정 위치

```csharp
public static async Task ProcessAsync(SessionComponent session, ReqLogin req)
{
    // ← 여기에 계정 검증 삽입 (서버 정원 체크 이전)
    // 1. BotToken 체크 → 통과 or 자동 계정 생성
    // 2. DB에서 username 조회
    // 3. 비밀번호 검증 (Phase 2: 평문, Phase 3: BCrypt.Verify)
    // 4. 실패 → ResLogin(INVALID_CREDENTIALS) return

    if (PlayerSystem.Instance.Count >= PlayerSystem.Instance.MaxPlayers)
    // ... 기존 코드 유지
```

## Phase 2 신규 파일 위치

| 파일 | 위치 |
|------|------|
| `AccountRow.cs` | `GameServer.Database/Rows/` |
| `AccountDbSet.cs` | `GameServer.Database/DbSets/` |
| `RegisterProcessor.cs` | `GameServer/Network/` |
| `AuthSettings.cs` | `Common.Server/` |
| `register.proto` | `GameServer.Protocol/Protos/` |

## Phase 2 봇 토큰 처리 흐름

```
req.Password == BotToken?
    YES → DB에 username 있음?
              YES → 해당 account_id 반환 (로그인)
              NO  → 신규 accounts INSERT (hash = BCrypt(BotToken)) 후 로그인
    NO  → DB에서 username 조회 → BCrypt.Verify → 실패 시 INVALID_CREDENTIALS
```

## Phase 3 BCrypt 변경 범위

```csharp
// RegisterProcessor — 변경 전 (Phase 2)
password_hash = req.Password   // 평문 저장

// RegisterProcessor — 변경 후 (Phase 3)
password_hash = BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 11)

// LoginProcessor — 변경 전 (Phase 2)
if (account.password_hash != req.Password) → INVALID_CREDENTIALS

// LoginProcessor — 변경 후 (Phase 3)
if (!BCrypt.Net.BCrypt.Verify(req.Password, account.password_hash)) → INVALID_CREDENTIALS
```

---

## 주요 파일 경로 참조

| 항목 | 경로 |
|------|------|
| 서버 파이프라인 | `GameServer/Network/GamePipelineInitializer.cs` |
| 로그인 처리 | `GameServer/Network/LoginProcessor.cs` |
| 서버 핸들러 | `GameServer/Network/GameServerHandler.cs` |
| 세션 컴포넌트 | `GameServer/Network/SessionComponent.cs` |
| DB 스키마 | `db/schema_game.sql` |
| 게임 DB 레이어 | `GameServer.Database/GameDatabase.cs` |
| 서버 설정 레코드 | `Common.Server/GameServerSettings.cs` |
| 부하 테스트 설정 | `GameClient/LoadTestConfig.cs` |
| 클라 컨텍스트 | `GameClient/Controllers/ClientContext.cs` |
| 클라 기본 시나리오 | `GameClient/Scenarios/BaseRoomScenario.cs` |
| 클라 진입점 | `GameClient/Program.cs` |
