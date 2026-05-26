namespace GameServer.Database.Rows;

/// <summary>password_reset_tokens 테이블 매핑 POCO.</summary>
public class PasswordResetTokenRow
{
    public ulong    token_id   { get; set; }
    public ulong    account_id { get; set; }
    /// <summary>SHA-256 hex 64자, 1회용.</summary>
    public string   token      { get; set; } = "";
    public DateTime expires_at { get; set; }
    public DateTime? used_at   { get; set; }
}
