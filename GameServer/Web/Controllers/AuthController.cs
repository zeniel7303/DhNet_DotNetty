using System.Security.Cryptography;
using Common.Logging;
using GameServer.Auth;
using GameServer.Database;
using GameServer.Database.Rows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(SmtpService smtp, IConfiguration config) : ControllerBase
{
    private const int MinPasswordLength = 4;
    private const int MaxPasswordLength = 16;

    [HttpPost("forgot-password")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        const string genericOk = "처리되었습니다. 계정이 확인되면 이메일이 발송됩니다.";

        // 만료 토큰 정리 (요청마다 실행하여 누적 방지)
        await DatabaseSystem.Instance.Game.PasswordResetTokens.DeleteExpiredAsync();

        // username 또는 email이 비어 있으면 조용히 성공 반환 (enumeration 방지)
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email))
            return Ok(new { message = genericOk });

        // 계정 조회 — username + email 양쪽이 일치해야 함
        var account = await DatabaseSystem.Instance.Game.Accounts.SelectByUsernameAsync(req.Username.Trim());
        if (account == null
            || string.IsNullOrEmpty(account.email)
            || !string.Equals(account.email, req.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // 계정 열거 공격 방지: 일치 여부를 응답에서 노출하지 않음
            return Ok(new { message = genericOk });
        }

        // 토큰 생성: SHA-256(Guid) → 64자 hex
        var rawBytes = SHA256.HashData(Guid.NewGuid().ToByteArray());
        var token    = Convert.ToHexString(rawBytes).ToLower();

        await DatabaseSystem.Instance.Game.PasswordResetTokens.InsertAsync(new PasswordResetTokenRow
        {
            account_id = account.account_id,
            token      = token,
            expires_at = DateTime.UtcNow.AddHours(1)
        });

        // 리셋 URL: 서버 설정값 사용 (클라이언트 제출값 신뢰하지 않음 — 피싱 방지)
        var baseUrl  = (config["Auth:ResetPasswordBaseUrl"] ?? "").TrimEnd('/');
        var resetUrl = string.IsNullOrEmpty(baseUrl)
            ? $"?reset_token={token}"
            : $"{baseUrl}?reset_token={token}";

        await smtp.SendPasswordResetAsync(account.email, resetUrl);

        GameLogger.Info("Auth", $"비밀번호 재설정 요청: {account.username} (account_id={account.account_id})");
        return Ok(new { message = genericOk });
    }

    [HttpPost("reset-password")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return BadRequest(new { error = "토큰이 필요합니다." });

        // M-1: NewPassword null 가드
        if (string.IsNullOrEmpty(req.NewPassword)
            || req.NewPassword.Length < MinPasswordLength
            || req.NewPassword.Length > MaxPasswordLength)
            return BadRequest(new { error = $"비밀번호는 {MinPasswordLength}~{MaxPasswordLength}자여야 합니다." });

        // 만료 토큰 정리
        await DatabaseSystem.Instance.Game.PasswordResetTokens.DeleteExpiredAsync();

        var row = await DatabaseSystem.Instance.Game.PasswordResetTokens.SelectByTokenAsync(req.Token.Trim());
        if (row == null || row.used_at.HasValue || row.expires_at < DateTime.UtcNow)
            return BadRequest(new { error = "유효하지 않거나 만료된 토큰입니다." });

        // C-1: 토큰을 먼저 원자적으로 소모 — 동시 요청에 의한 이중 사용 방지
        // MarkUsedConditionalAsync: used_at IS NULL AND 미만료인 경우에만 UPDATE
        var marked = await DatabaseSystem.Instance.Game.PasswordResetTokens.MarkUsedConditionalAsync(row.token_id);
        if (marked == 0)
            return BadRequest(new { error = "유효하지 않거나 만료된 토큰입니다." });

        // 토큰 소모 확정 후 BCrypt 해시 — ThreadPool 분리 (블로킹 연산)
        var newHash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(req.NewPassword, workFactor: AuthConstants.BcryptWorkFactor));
        await DatabaseSystem.Instance.Game.Accounts.UpdatePasswordHashAsync(row.account_id, newHash);

        GameLogger.Info("Auth", $"비밀번호 재설정 완료: account_id={row.account_id}");
        return Ok(new { message = "비밀번호가 변경되었습니다." });
    }
}

public record ForgotPasswordRequest(string Username, string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
