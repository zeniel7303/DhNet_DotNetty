using Common.Logging;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace GameServer.Auth;

/// <summary>
/// SMTP 이메일 발송 서비스.
/// appsettings.json Smtp 섹션이 설정되지 않으면 로그만 출력하고 건너뛴다 (개발 편의).
/// </summary>
public class SmtpService
{
    private readonly string               _host;
    private readonly int                  _port;
    private readonly string               _username;
    private readonly string               _password;
    private readonly string               _senderEmail;
    private readonly string               _senderName;
    private readonly SecureSocketOptions  _secureSocket;
    private readonly bool                 _enabled;

    public SmtpService(IConfiguration config)
    {
        _host        = config["Smtp:Host"]        ?? "";
        _port        = int.TryParse(config["Smtp:Port"], out var p) ? p : 587;
        _username    = config["Smtp:Username"]    ?? "";
        _password    = config["Smtp:Password"]    ?? "";
        _senderEmail = config["Smtp:SenderEmail"] ?? _username;
        _senderName  = config["Smtp:SenderName"]  ?? "GameServer";
        _secureSocket = config["Smtp:SecureSocket"] switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None"         => SecureSocketOptions.None,
            _              => SecureSocketOptions.StartTls   // Gmail 587 기본값
        };
        _enabled = !string.IsNullOrEmpty(_host) && !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password);

        if (!_enabled)
            GameLogger.Warn("Smtp", "SMTP 설정 누락 — 이메일 발송 비활성화");
    }

    /// <summary>
    /// 비밀번호 재설정 이메일 발송.
    /// SMTP 비활성화 시 false 반환 (호출자는 무시하고 정상 응답).
    /// </summary>
    public async Task<bool> SendPasswordResetAsync(string toEmail, string resetUrl)
    {
        if (!_enabled) return false;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_senderName, _senderEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "[GameServer] 비밀번호 재설정";
        message.Body    = new TextPart("html")
        {
            Text = $@"
<p>안녕하세요,</p>
<p>비밀번호 재설정을 요청하셨습니다. 아래 링크를 클릭하면 새 비밀번호를 설정할 수 있습니다.</p>
<p><a href=""{resetUrl}"">{resetUrl}</a></p>
<p>링크는 <strong>1시간</strong> 동안 유효합니다.</p>
<p>본인이 요청하지 않았다면 이 메일을 무시하세요.</p>"
        };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_host, _port, _secureSocket);
            await client.AuthenticateAsync(_username, _password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
            GameLogger.Info("Smtp", $"비밀번호 재설정 메일 발송: {toEmail}");
            return true;
        }
        catch (Exception ex)
        {
            GameLogger.Error("Smtp", $"메일 발송 실패: {toEmail}", ex);
            return false;
        }
    }
}
