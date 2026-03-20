namespace GameServer.Database.Rows;

/// <summary>players 테이블 매핑 POCO.</summary>
public class PlayerRow
{
    public ulong     player_id   { get; set; }
    public string    player_name { get; set; } = "";
    public DateTime  login_at    { get; set; }
    public DateTime? logout_at   { get; set; }  // null = 현재 온라인
    public string?   ip_address  { get; set; }
    public ulong?    account_id  { get; set; }  // accounts.account_id FK
}
