namespace GameServer.Database.Rows;

/// <summary>accounts 테이블 매핑 POCO.</summary>
public class AccountRow
{
    public ulong    account_id    { get; set; }
    public string   username      { get; set; } = "";
    /// <summary>BCrypt(workFactor=11) 해시.</summary>
    public string   password_hash { get; set; } = "";
    public DateTime created_at    { get; set; }
}
