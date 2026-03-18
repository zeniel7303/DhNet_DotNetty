using Common;
using Common.Logging;
using GameServer.Database;
using GameServer.Network;
using GameServer.Systems;
using GameServer.Web;
using Microsoft.Extensions.Configuration;

namespace GameServer;

static class ServerStartup
{
    public static async Task RunAsync(IConfiguration config)
    {
        var gameSettings = config.GetSection("GameServer").Get<GameServerSettings>()
            ?? throw new InvalidOperationException("appsettings.json에 'GameServer' 섹션이 없습니다.");
        var dbSettings = config.GetSection("Database").Get<DatabaseSettings>()
            ?? throw new InvalidOperationException("appsettings.json에 'Database' 섹션이 없습니다.");

        PlayerSystem.Instance.Initialize(gameSettings.MaxPlayers);
        SessionSystem.Instance.StartSystem();
        PlayerSystem.Instance.StartSystem();

        var dbResult = await DatabaseSystem.Instance.InitializeAsync(dbSettings);
        IdGenerators.Player.Initialize(dbResult.MaxPlayerId);
        IdGenerators.Room.Initialize(dbResult.MaxRoomId);
        GameLogger.Info("Server", $"IdGenerators 초기화: Player={dbResult.MaxPlayerId}, Room={dbResult.MaxRoomId}");

        LobbySystem.Instance.Initialize(lobbyCount: 10, lobbyCapacity: gameSettings.MaxPlayers);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var statTask = StatLogger.RunAsync(cts.Token);
        var webTask  = WebServerHost.RunAsync(gameSettings.WebPort, cts.Token);
        await using var server = new GameServerBootstrap(gameSettings);
        await server.RunAsync(cts.Token);
        await Task.WhenAll(statTask, webTask);
        await GameLogger.FlushAsync();
    }
}
