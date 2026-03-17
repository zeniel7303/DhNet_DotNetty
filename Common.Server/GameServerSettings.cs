using Common.Server;

namespace Common;

public class GameServerSettings
{
    public int GamePort   { get; set; } = 7777;
    public int WebPort    { get; set; } = 8080;
    public int MaxPlayers { get; set; } = ServerConstants.MaxPlayers;
}
