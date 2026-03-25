namespace GameServer.Database.Rows;

/// <summary>
/// characters 테이블 매핑 POCO.
/// 인게임 스탯(level, exp, hp 등)은 게임마다 새로 초기화되므로 저장하지 않는다.
/// 영속 데이터: gold만 저장.
/// </summary>
public class CharacterRow
{
    public ulong account_id { get; set; }
    public int   gold       { get; set; } = 0;
}
