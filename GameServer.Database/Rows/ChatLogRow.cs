namespace GameServer.Database.Rows;

/// <summary>
/// chat_logs 테이블 매핑 POCO.
/// channel: "lobby" | "room"
/// room_id: null = 로비 채팅
/// </summary>
public class ChatLogRow
{
    public ulong player_id { get; set; }
    public ulong? room_id { get; set; }
    public string channel { get; set; } = "";
    public string message { get; set; } = "";
    public DateTime created_at { get; set; }
}
