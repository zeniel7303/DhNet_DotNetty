using Common;
using Common.Logging;
using GameServer;

Helper.SetConsoleLogger();
var config = AppConfig.Build(args);

try
{
    await ServerStartup.RunAsync(config);
}
catch (Exception ex)
{
    GameLogger.Error("Startup", "서버 초기화 실패 — 프로세스를 종료합니다.", ex);
    await GameLogger.FlushAsync();
    Environment.Exit(1);
}
