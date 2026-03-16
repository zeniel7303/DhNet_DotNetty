using Microsoft.Extensions.Configuration;

namespace GameServer;

static class AppConfig
{
    public static IConfiguration Build(string[] args)
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables("GAMESERVER_")
            .AddCommandLine(args)
            .Build();
}
