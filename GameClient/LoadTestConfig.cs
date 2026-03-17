namespace GameClient;

public record LoadTestConfig
{
    public int ClientCount         { get; init; } = 1;
    public int ConnectDelayMs      { get; init; } = 10;
    public int ChatIntervalMs      { get; init; } = 1000;
    public string Scenario         { get; init; } = "lobby";
    public string ServerHost       { get; init; } = "127.0.0.1";
    public int ServerPort          { get; init; } = 7777;
    public string PlayerNamePrefix { get; init; } = "Bot";
    /// <summary>재접속 대기 시간(ms). reconnect-stress 전용. jitter ±200ms 자동 적용.</summary>
    public int ReconnectDelayMs    { get; init; } = 2000;
    /// <summary>룸 사이클 한도. 0=무한. reconnect-stress 전용.</summary>
    public int RoomCycles          { get; init; } = 0;
    /// <summary>룸 입장 후 보낼 채팅 수. reconnect-stress 전용.</summary>
    public int ChatCount           { get; init; } = 3;

    public static LoadTestConfig FromArgs(string[] args)
    {
        int clientCount = 4, connectDelay = 10, chatInterval = 1000, port = 7777;
        int reconnectDelay = 2000, roomCycles = 0, chatCount = 3;
        string scenario = "room", host = "127.0.0.1", prefix = "Bot";

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--clients":          int.TryParse(args[++i], out clientCount);    break;
                case "--delay":            int.TryParse(args[++i], out connectDelay);   break;
                case "--interval":         int.TryParse(args[++i], out chatInterval);   break;
                case "--port":             int.TryParse(args[++i], out port);           break;
                case "--reconnect-delay":  int.TryParse(args[++i], out reconnectDelay); break;
                case "--room-cycles":      int.TryParse(args[++i], out roomCycles);     break;
                case "--chat-count":       int.TryParse(args[++i], out chatCount);      break;
                case "--host":             host     = args[++i]; break;
                case "--scenario":         scenario = args[++i]; break;
                case "--prefix":           prefix   = args[++i]; break;
            }
        }

        return new LoadTestConfig
        {
            ClientCount       = clientCount,
            ConnectDelayMs    = connectDelay,
            ChatIntervalMs    = chatInterval,
            Scenario          = scenario,
            ServerHost        = host,
            ServerPort        = port,
            PlayerNamePrefix  = prefix,
            ReconnectDelayMs  = reconnectDelay,
            RoomCycles        = roomCycles,
            ChatCount         = chatCount,
        };
    }
}
