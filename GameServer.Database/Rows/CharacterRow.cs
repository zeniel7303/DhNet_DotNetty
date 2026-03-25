namespace GameServer.Database.Rows;

/// <summary>characters 테이블 매핑 POCO.</summary>
public class CharacterRow
{
    public ulong  account_id { get; set; }
    public int    level      { get; set; } = 1;
    public long   exp        { get; set; } = 0;
    public int    hp         { get; set; } = 500;
    public int    max_hp     { get; set; } = 500;
    public int    attack     { get; set; } = 20;
    public int    defense    { get; set; } = 10;
    public float  x          { get; set; } = 100f;
    public float  y          { get; set; } = 100f;
}
