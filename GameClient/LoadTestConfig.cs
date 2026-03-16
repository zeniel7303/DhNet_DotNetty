namespace GameClient;

public record LoadTestConfig
{
    public int ClientCount       { get; init; } = 1;
    public int ConnectDelayMs    { get; init; } = 10;
    public int ChatIntervalMs    { get; init; } = 1000;
    public string Scenario       { get; init; } = "lobby";
    public string ServerHost     { get; init; } = "127.0.0.1";
    public int ServerPort        { get; init; } = 7777;
    public string PlayerNamePrefix { get; init; } = "Bot";

    public static LoadTestConfig FromArgs(string[] args)
    {
        int clientCount = 4, connectDelay = 10, chatInterval = 1000, port = 7777;
        string scenario = "room", host = "127.0.0.1", prefix = "Bot";

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--clients":   int.TryParse(args[++i], out clientCount);   break;
                case "--delay":     int.TryParse(args[++i], out connectDelay);  break;
                case "--interval":  int.TryParse(args[++i], out chatInterval);  break;
                case "--port":      int.TryParse(args[++i], out port);          break;
                case "--host":      host    = args[++i]; break;
                case "--scenario":  scenario = args[++i]; break;
                case "--prefix":    prefix  = args[++i]; break;
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
            PlayerNamePrefix  = prefix
        };
    }
}
