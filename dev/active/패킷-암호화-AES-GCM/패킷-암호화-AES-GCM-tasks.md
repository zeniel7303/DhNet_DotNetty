# 패킷 암호화 AES-GCM — 작업 체크리스트

Last Updated: 2026-03-20
참고: dev/active/보안-3단계-구현/보안-3단계-구현-plan.md (Phase 1 상세)

## 작업 순서

- ✅ 1.1 `Common.Server/EncryptionSettings.cs` 생성
- ✅ 1.2 `GameServer/appsettings.json` — Encryption 섹션 추가 (Key: "" 비활성화 상태)
- ✅ 1.3 `Common.Shared/Crypto/AesGcmCryptor.cs` 구현 (알고리즘 선택 근거 주석 포함)
- ✅ 1.4 `GameServer/Network/AesGcmDecryptionHandler.cs` + `AesGcmEncryptionHandler.cs` 구현
- ✅ 1.5 `GameServer/Network/GamePipelineInitializer.cs` — 핸들러 삽입
- ✅ 1.6 `GameServer/Network/GameServerBootstrap.cs` — EncryptionSettings 파라미터 추가
- ✅ 1.7 `GameServer/ServerStartup.cs` — EncryptionSettings 읽기
- ✅ 1.8 `GameClient/Network/AesGcmDecryptionHandler.cs` + `AesGcmEncryptionHandler.cs` 구현
- ✅ 1.9 `GameClient/LoadTestConfig.cs` — EncryptionKey 필드 + --encryption-key CLI 인자
- ✅ 1.10 `GameClient/Program.cs` — 두 파이프라인 모두 crypto 핸들러 삽입
- ✅ 1.11 빌드 확인 (경고 0, 오류 0)
- ✅ 1.12 코드 리뷰 후 Program.cs Fail-fast 키 검증 수정 (RunAsync 시점 조기 파싱)
- ✅ 1.13 서버/클라 기본 키 동기화 (appsettings.json ↔ LoadTestConfig 동일 값 설정)
- ✅ 1.14 부하 테스트 통과 (1000 클라이언트, lobby-chat, 30초, 에러 0)

## 상태: ✅ 완료
