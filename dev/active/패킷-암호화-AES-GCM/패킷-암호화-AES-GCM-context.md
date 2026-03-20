# 패킷 암호화 AES-GCM — 컨텍스트

Last Updated: 2026-03-20

---

## 현재 구현 상태: ✅ 완료 (빌드 성공, 경고 0 오류 0)

---

## 이 세션에서 수정된 파일

| 파일 | 변경 유형 | 내용 |
|------|----------|------|
| `Common.Shared/Crypto/AesGcmCryptor.cs` | 신규 | 순수 암호화 유틸 (DotNetty 의존 없음) |
| `Common.Server/EncryptionSettings.cs` | 신규 | appsettings 바인딩 클래스 |
| `GameServer/Network/AesGcmDecryptionHandler.cs` | 신규 | 서버 인바운드 복호화 핸들러 |
| `GameServer/Network/AesGcmEncryptionHandler.cs` | 신규 | 서버 아웃바운드 암호화 핸들러 |
| `GameServer/Network/GamePipelineInitializer.cs` | 수정 | EncryptionSettings 받아 crypto 핸들러 삽입 |
| `GameServer/Network/GameServerBootstrap.cs` | 수정 | EncryptionSettings 생성자 파라미터 추가 |
| `GameServer/ServerStartup.cs` | 수정 | EncryptionSettings 읽기 + 경고 로그 |
| `GameServer/appsettings.json` | 수정 | Encryption 섹션 추가, 기본 키 설정 |
| `GameClient/Network/AesGcmDecryptionHandler.cs` | 신규 | 클라 인바운드 복호화 핸들러 |
| `GameClient/Network/AesGcmEncryptionHandler.cs` | 신규 | 클라 아웃바운드 암호화 핸들러 |
| `GameClient/LoadTestConfig.cs` | 수정 | EncryptionKey 필드 + --encryption-key CLI 인자 |
| `GameClient/Program.cs` | 수정 | 두 파이프라인(일반/reconnect-stress)에 crypto 핸들러 삽입 |

---

## 주요 아키텍처 결정사항

### 1. 파일 배치 전략
- `AesGcmCryptor` → `Common.Shared/Crypto/` (System.Security.Cryptography만 사용, DotNetty 불필요)
- `EncryptionSettings` → `Common.Server/` (기존 GameServerSettings 패턴 동일)
- DotNetty 핸들러 → 각 프로젝트 `Network/` 별도 (Common.Shared는 DotNetty.Codecs 없음)
  - Common.Shared에 DotNetty.Codecs 추가하는 것보다 2파일 복사가 더 단순

### 2. 파이프라인 삽입 위치
```
framing-dec → [crypto-dec] → [crypto-enc] → protobuf-decoder
                                            ← protobuf-encoder ←
```
- framing-dec 직후, protobuf-decoder 직전
- 이유: framing이 먼저 패킷 경계를 분리한 뒤 암호화 레이어가 처리

### 3. 비활성화 모드
- `EncryptionSettings.Key = ""` → crypto 핸들러 파이프라인에 추가 안 함
- 서버 시작 시 경고 로그 출력
- 클라도 동일: `config.EncryptionKey = ""` → encKey = null → 핸들러 생략

### 4. 변조 패킷 처리
- auth-tag 불일치 → `AuthenticationTagMismatchException`
- `AesGcmDecryptionHandler.ExceptionCaught` 에서 캐치 → 경고 로그 + `ctx.CloseAsync()`

---

## 현재 설정 상태

서버와 클라이언트 기본값이 동일한 키로 설정되어 있어 인자 없이 실행해도 암호화 동작.

`GameServer/appsettings.json`:
```json
"Encryption": {
  "Key": "AiZROpbIadx1uVbp64v7nQ=="
}
```

`GameClient/LoadTestConfig.cs`:
```csharp
public string EncryptionKey { get; init; } = "AiZROpbIadx1uVbp64v7nQ==";
```

### 키 교체 방법 (운영 배포 시)
1. 새 키 생성: `node -e "require('crypto').randomBytes(16).toString('base64') |> console.log"`
   또는 C#: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))`
2. `appsettings.json`의 `Encryption.Key` 교체
3. 클라이언트 실행 시 `--encryption-key <새 키>` 인자 전달

### 결정 기록: SHA-256 방식 시도 후 롤백
- 시도: 임의 문자열 → SHA-256 → 16바이트 방식 (`CHANGE-THIS-ENCRYPTION-KEY` 같은 가독성 있는 문자열 사용 목적)
- 롤백 이유: 원래 Base64 방식이 의도된 설계였으므로 그대로 유지
- 최종: Base64 방식 + 서버/클라 동일 기본값으로 테스트 용이성 확보

---

## 부하 테스트 결과 (2026-03-20)

환경: 로컬 머신, 1000 클라이언트, lobby-chat 시나리오, 30초

| 구간 | 암호화 ON | 암호화 OFF |
|------|-----------|-----------|
| 5s Active | 945 | 957 |
| 10s Active | 1000 (전원 접속) | 1000 (전원 접속) |
| 25s ChatRecv | 7,853,911 | 8,587,297 |
| Errors | 0 | 0 |

- 처리량 암호화 ON: 약 **377,000 패킷/s**
- 처리량 암호화 OFF: 약 **429,000 패킷/s**
- 암호화 오버헤드: **약 12%** (AES-GCM 암호화/복호화 + Nonce 랜덤 생성 비용)
- 에러 0개 — 게임 서버 수준에서 허용 가능한 범위

---

## 다음 단계: Phase 2 (회원가입 및 비밀번호)

참조: `dev/active/보안-3단계-구현/보안-3단계-구현-plan.md` Phase 2 섹션

Phase 2 시작 전 확인사항:
- [ ] Phase 1 키 설정 후 실제 봇 부하 테스트 통과 확인
- [ ] Phase 2 DB 스키마 변경 (accounts 테이블)
