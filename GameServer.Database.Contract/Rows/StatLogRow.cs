namespace GameServer.Database.Rows;

/// <summary>
/// stat_logs 테이블 매핑 POCO.
/// N초 주기로 현재 접속 중인 플레이어 수를 기록한다.
/// </summary>
public class StatLogRow
{
    public int player_count { get; set; }
    public DateTime created_at { get; set; }
}
