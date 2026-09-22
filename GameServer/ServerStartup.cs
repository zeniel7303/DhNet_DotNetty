using Common;
using Common.Logging;
using GameServer.Database;
using GameServer.Database.Gateway;
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

        var walDirectory = Path.Combine(AppContext.BaseDirectory, "db-wal");
        var gateway = LocalDbGateway.Create(DatabaseSystem.Instance, walDirectory);
        gateway.OnlineCount = () => PlayerSystem.Instance.Count;
        gateway.OnSustainedOutage = ForceDisconnectDirty;
        gateway.OnWalWriteFailed = ForceDisconnectAccount;
        DbGateway.Use(gateway);
        gateway.Start();

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
        await gateway.DisposeAsync();

        GameLogger.Info("Server", "[Shutdown] Web/WS 서버, Stat 로거 종료 대기...");
        await Task.WhenAll(statTask, webTask, wsTask);

        GameLogger.Info("Server", "[Shutdown] 완료.");
        await GameLogger.FlushAsync();
    }

    /// <summary>
    /// DB에 반영되지 못한 계정과, 메모리에만 남은 dirty 세션을 끊는다.
    /// 끊김 경로가 골드·로그아웃을 WAL에 남긴 뒤 PlayerSystem에서 제거한다.
    /// </summary>
    private static void ForceDisconnectDirty(IReadOnlyList<ulong> parkedAccountIds)
    {
        var parked = new HashSet<ulong>(parkedAccountIds);
        foreach (var player in PlayerSystem.Instance.GetAll())
        {
            if (parked.Contains(player.AccountId) || player.Save.IsDirty)
            {
                player.DisconnectForNextTick();
            }
        }
    }

    private static void ForceDisconnectAccount(ulong accountId)
        => PlayerSystem.Instance.TryGet(accountId)?.DisconnectForNextTick();

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
