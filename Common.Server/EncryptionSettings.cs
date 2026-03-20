using System.Security.Cryptography;

namespace Common;

/// <summary>
/// appsettings.json "Encryption" 섹션 바인딩.
///
/// Key가 빈 문자열이면 암호화 비활성화.
///
/// [키 형식]
///   Base64 인코딩된 정확히 16바이트 문자열 (AES-128).
///   생성: Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
///
/// [Pre-shared key 방식 채택 이유]
///   - 게임 클라이언트를 서버가 직접 배포 → 키 배포 채널이 통제됨
///   - ECDH 세션 키 방식 대비 구현 단순 (향후 업그레이드 가능)
/// </summary>
public class EncryptionSettings
{
    /// <summary>
    /// AES-128 pre-shared key (Base64, 정확히 16바이트).
    /// 빈 문자열 = 암호화 비활성화.
    /// 서버와 클라이언트에 동일한 값을 설정해야 함.
    /// </summary>
    public string Key { get; set; } = "";

    public bool IsEnabled => !string.IsNullOrEmpty(Key);

    public byte[] GetKeyBytes()
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Key); }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Encryption.Key가 유효한 Base64 형식이 아닙니다.");
        }

        if (bytes.Length != 16)
            throw new InvalidOperationException(
                $"Encryption.Key는 정확히 16바이트(AES-128)여야 합니다. 현재: {bytes.Length}바이트.");

        return bytes;
    }
}
