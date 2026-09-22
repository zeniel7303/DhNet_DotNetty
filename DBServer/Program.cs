using System.Net;
using Common;
using Common.Logging;
using DBServer;
using GameServer.Database;
using GameServer.Database.Gateway;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Helper.SetConsoleLogger();

var builder = WebApplication.CreateBuilder(args);
var dbSettings = builder.Configuration.GetSection("Database").Get<DatabaseSettings>()
    ?? throw new InvalidOperationException("appsettings.json에 'Database' 섹션이 없습니다.");
var serverSettings = builder.Configuration.GetSection("DbServer").Get<DbServerSettings>()
    ?? new DbServerSettings();

if (serverSettings.UseTls && string.IsNullOrWhiteSpace(serverSettings.CertificatePath))
{
    throw new InvalidOperationException("TLS를 쓰려면 DbServer:CertificatePath가 필요합니다.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    var address = serverSettings.BindAddress is "0.0.0.0" or "*"
        ? IPAddress.Any
        : IPAddress.Parse(serverSettings.BindAddress);

    // TLS가 없으면 한 포트에서 HTTP/1.1과 HTTP/2를 협상할 수 없다.
    // gRPC는 HTTP/2 전용 포트, /health는 HTTP/1.1 포트로 나눈다.
    if (serverSettings.UseTls)
    {
        options.Listen(address, serverSettings.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(serverSettings.CertificatePath, serverSettings.CertificatePassword);
        });
    }
    else
    {
        var healthPort = serverSettings.HealthPort > 0 ? serverSettings.HealthPort : serverSettings.Port + 1;
        if (healthPort == serverSettings.Port)
        {
            throw new InvalidOperationException("TLS를 끄면 DbServer:HealthPort는 gRPC 포트와 달라야 합니다.");
        }

        options.Listen(address, serverSettings.Port, listen => listen.Protocols = HttpProtocols.Http2);
        options.Listen(address, healthPort, listen => listen.Protocols = HttpProtocols.Http1);
    }
});
builder.WebHost.PreferHostingUrls(false);

var dbResult = await DatabaseSystem.Instance.InitializeAsync(dbSettings);
GameLogger.Info("DBServer", $"MySQL 준비. Account={dbResult.MaxAccountId}, Room={dbResult.MaxRoomId}");

var walDirectory = string.IsNullOrWhiteSpace(serverSettings.WalDirectory)
    ? Path.Combine(AppContext.BaseDirectory, "db-wal")
    : serverSettings.WalDirectory;
var gateway = LocalDbGateway.Create(DatabaseSystem.Instance, walDirectory);
var online = new OnlineCountSource();
var hub = new GatewaySignalHub();
gateway.OnlineCount = () => online.Count;
gateway.OnLoginBlockedChanged = hub.PublishLoginBlocked;
gateway.OnSustainedOutage = hub.PublishOutage;
gateway.OnWalWriteFailed = hub.PublishWalFailure;
gateway.Start();

builder.Services.AddSingleton(DatabaseSystem.Instance);
builder.Services.AddSingleton<IDbGateway>(gateway);
builder.Services.AddSingleton<IDbIdSeeds>(new DatabaseIdSeeds(DatabaseSystem.Instance));
builder.Services.AddSingleton(online);
builder.Services.AddSingleton(hub);
builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks()
    .AddCheck<MysqlHealthCheck>("mysql");

var app = builder.Build();
app.MapGrpcService<DbGatewayGrpcService>();
app.MapGrpcHealthChecksService();
app.MapHealthChecks("/health");
app.Lifetime.ApplicationStopping.Register(() =>
{
    gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

var scheme = serverSettings.UseTls ? "https" : "http";
var healthPort = serverSettings.UseTls
    ? serverSettings.Port
    : serverSettings.HealthPort > 0 ? serverSettings.HealthPort : serverSettings.Port + 1;
GameLogger.Info(
    "DBServer",
    $"gRPC {scheme}://{serverSettings.BindAddress}:{serverSettings.Port} health {scheme}://{serverSettings.BindAddress}:{healthPort}/health TLS={serverSettings.UseTls} WAL={walDirectory}");
app.Run();
