namespace GameServer.Database.Rows;

/// <summary>
/// room_logs 테이블 매핑 POCO.
/// action 값: "enter" | "exit" | "disconnect"
/// </summary>
public class RoomLogRow
{
    public ulong account_id { get; set; }
    public ulong room_id { get; set; }
    public string action { get; set; } = "";
    public DateTime created_at { get; set; }
}
