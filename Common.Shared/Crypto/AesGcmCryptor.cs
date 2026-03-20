using System.Security.Cryptography;

namespace Common.Crypto;

/// <summary>
/// AES-128-GCM 암호화/복호화 유틸리티.
///
/// ── 알고리즘 선택 근거 ───────────────────────────────────────────────
/// [AES-GCM vs 경쟁 알고리즘]
///   - AES-128-GCM 채택:
///       · .NET AesGcm 내부에서 하드웨어 AES-NI 명령어 자동 활용
///         → 데스크탑/서버 환경에서 ChaCha20-Poly1305보다 빠름
///       · ChaCha20-Poly1305는 AES-NI 없는 모바일/임베디드 환경 최적화
///         → 게임 서버에는 불필요
///   - AES-128 vs AES-256:
///       · 128-bit 키는 2^128 경우의 수 → 현존 양자컴퓨터로도 해독 불가
///       · 256-bit 키는 게임 서버 수준에서 과잉 보안이며 연산 비용 증가
///
/// [GCM(Galois/Counter Mode) 선택 근거]
///   - AEAD(Authenticated Encryption with Associated Data):
///       · 암호화(기밀성) + 무결성(변조 감지)을 단일 알고리즘으로 처리
///       · AES-CBC + HMAC 조합보다 구현 단순하고 실수 여지 없음
///   - AES-CBC 미채택 이유:
///       · 패딩 필요 → Padding Oracle 공격 취약점 존재
///       · 무결성 없음 → 변조 패킷을 탐지하려면 HMAC 별도 추가 필요
///   - AES-CTR 미채택: 무결성 없음 (변조 패킷 감지 불가)
///
/// [Nonce 설계 근거]
///   - 12바이트(96-bit): GCM 표준 권장 크기
///       · 다른 길이는 GHASH 함수로 96-bit로 변환하는 추가 연산 발생
///   - 패킷마다 RandomNumberGenerator.Fill → Nonce 재사용 완전 방지
///       · Nonce 재사용은 GCM의 유일한 치명적 약점 (키 복구 가능)
///       · Cryptographically Secure Random 사용으로 예측 불가
///
/// ── 패킷 포맷 ────────────────────────────────────────────────────────
/// [2B length field] [12B nonce] [N bytes ciphertext] [16B auth-tag]
///                  └──────────── AesGcmCryptor 담당 ───────────────┘
///   auth-tag: 복호화 시 불일치 → AuthenticationTagMismatchException
///             → 변조/재전송 패킷 즉시 감지
/// ─────────────────────────────────────────────────────────────────────
/// </summary>
public static class AesGcmCryptor
{
    private const int NonceSize = 12; // GCM 표준 Nonce (96-bit)
    private const int TagSize   = 16; // GCM 인증 태그 (128-bit)

    /// <summary>
    /// plaintext를 AES-128-GCM으로 암호화.
    /// 반환값 포맷: [12B nonce | ciphertext | 16B auth-tag]
    /// </summary>
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];

        var nonce      = output.AsSpan(0, NonceSize);
        var ciphertext = output.AsSpan(NonceSize, plaintext.Length);
        var tag        = output.AsSpan(NonceSize + plaintext.Length, TagSize);

        // 패킷마다 새 Nonce 생성 → Nonce 재사용 방지 (GCM 유일한 약점 차단)
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return output;
    }

    /// <summary>
    /// [12B nonce | ciphertext | 16B auth-tag] 포맷의 데이터를 복호화.
    /// auth-tag 불일치(변조/재전송) 시 AuthenticationTagMismatchException.
    /// </summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException(
                $"암호화 데이터 길이 부족: 최소 {NonceSize + TagSize}B 필요, 수신 {data.Length}B.");

        var nonce      = data[..NonceSize];
        var tag        = data[^TagSize..];
        var ciphertext = data[NonceSize..^TagSize];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        // auth-tag 불일치 → AuthenticationTagMismatchException
        // → AesGcmDecryptionHandler.ExceptionCaught 에서 연결 해제
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
