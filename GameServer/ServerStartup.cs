using Common;
using Common.Logging;
using GameServer.Database;
using GameServer.Network;
using GameServer.Resources;
using GameServer.Systems;
using GameServer.Web;
using Microsoft.Extensions.Configuration;

namespace GameServer;

internal static class ServerStartup
{
    public static async Task RunAsync(IConfiguration config)
    {
        var gameSettings = config.GetSection("GameServer").Get<GameServerSettings>()
            ?? throw new InvalidOperationException("appsettings.json에 'GameServer' 섹션이 없습니다.");
        var dbSettings = config.GetSection("Database").Get<DatabaseSettings>()
            ?? throw new InvalidOperationException("appsettings.json에 'Database' 섹션이 없습니다.");
        var encSettings = config.GetSection("Encryption").Get<EncryptionSettings>()
            ?? new EncryptionSettings();
        if (!encSettings.IsEnabled)
            GameLogger.Warn("Server", "[Encryption] Key가 비어있어 암호화가 비활성화되었습니다.");

        var dbResult = await DatabaseSystem.Instance.InitializeAsync(dbSettings);
        IdGenerators.Account.Initialize(dbResult.MaxAccountId);
        IdGenerators.Room.Initialize(dbResult.MaxRoomId);
        GameLogger.Info("Server", $"IdGenerators 초기화: Account={dbResult.MaxAccountId}, Room={dbResult.MaxRoomId}");

        var resourceDir = FindResourceDir();
        GameLogger.Info("Server", $"GameDataTable 로드 시작: {resourceDir}");
        try
        {
            GameDataTable.Load(resourceDir);
        }
        catch (Exception ex)
        {
            GameLogger.Error("Server", $"GameDataTable 로드 실패 — 리소스 디렉터리: {resourceDir}", ex);
            await GameLogger.FlushAsync();
            throw;
        }
        GameLogger.Info("Server", $"GameDataTable 로드 완료: 몬스터 {GameDataTable.Monsters.Count}종, 무기 {GameDataTable.Weapons.Count}종, 웨이브 {GameDataTable.Waves.Length}개");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        GameSystems.Start(gameSettings, cts);

        var statTask = StatLogger.RunAsync(cts.Token);
        var webTask  = WebServerHost.RunAsync(gameSettings.WebPort, cts.Token);
        await using var wsServer = new WsServerBootstrap(gameSettings.WsPort);
        var wsTask = wsServer.RunAsync(cts.Token);
        await using var server = new GameServerBootstrap(gameSettings, encSettings);
        await server.RunAsync(cts.Token);

        await GameSystems.StopAsync();

        GameLogger.Info("Server", "[Shutdown] Web/WS 서버, Stat 로거 종료 대기...");
        await Task.WhenAll(statTask, webTask, wsTask);

        GameLogger.Info("Server", "[Shutdown] 완료.");
        await GameLogger.FlushAsync();
    }

    /// <summary>
    /// 솔루션 루트의 Bin/resources/ 를 우선 사용한다.
    /// 못 찾으면 실행 파일 옆 Resources/ 로 폴백 (Docker 등 배포 환경).
    /// </summary>
    private static string FindResourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "Bin", "resources");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Resources");
    }
}
