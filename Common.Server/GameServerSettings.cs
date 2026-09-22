using Common.Server;

namespace Common;

public class GameServerSettings
{
    public int GamePort          { get; set; } = 7777;
    public int WsPort            { get; set; } = 7778;
    public int WebPort           { get; set; } = 8080;
    public int MaxPlayers        { get; set; } = ServerConstants.MaxPlayers;
    public int MaxPlayersPerRoom { get; set; } = 2;

    /// <summary>
    /// BeginDisconnect 이후 SnapshotQueued 대기 시간(초).
    /// 초기값은 로그인 동기 경로와 같은 3초.
    /// </summary>
    public int SnapshotQueuedTimeoutSeconds { get; set; } = 3;

    public string WorldId { get; set; } = "main";
    public string ZoneId  { get; set; } = "room-map";
}
