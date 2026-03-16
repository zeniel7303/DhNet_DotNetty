using Common;
using GameServer;

Helper.SetConsoleLogger();
var config = AppConfig.Build(args);
await ServerStartup.RunAsync(config);
