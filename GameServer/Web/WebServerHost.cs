using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameServer.Web;

static class WebServerHost
{
    public static async Task RunAsync(int port, CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"
            });

            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            app.MapControllers();

            ct.Register(app.Lifetime.StopApplication);

            Common.Logging.GameLogger.Info("WebServer", $"WebServer started on port {port}");
            await app.RunAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Common.Logging.GameLogger.Error("WebServer", $"WebServer 시작 실패: {ex.Message}", ex);
        }
    }
}
