namespace GameServer.Database.Rows;

/// <summary>accounts 테이블 매핑 POCO.</summary>
public class AccountRow
{
    public ulong    account_id    { get; set; }
    public string   username      { get; set; } = "";
    /// <summary>Phase 2: 평문. Phase 3(BCrypt)에서 해시로 교체 예정.</summary>
    public string   password_hash { get; set; } = "";
    public DateTime created_at    { get; set; }
}
