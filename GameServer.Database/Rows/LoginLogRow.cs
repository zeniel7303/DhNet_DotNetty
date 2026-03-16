namespace GameServer.Database.Rows;

/// <summary>
/// login_logs 테이블 매핑 POCO.
/// players 테이블과 달리 로그인마다 새 row를 삽입하여 세션 이력을 보관한다.
/// logout_at = null → 비정상 종료(Disconnect 시 UPDATE로 채움).
/// </summary>
public class LoginLogRow
{
    public ulong player_id { get; set; }
    public string player_name { get; set; } = "";
    public string? ip_address { get; set; }
    public DateTime login_at { get; set; }
    public DateTime? logout_at { get; set; }
}
