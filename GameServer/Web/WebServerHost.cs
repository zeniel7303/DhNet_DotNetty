using System.Net;
using GameServer.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

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
            builder.Services.AddSwaggerGen(o =>
            {
                var scheme = new OpenApiSecurityScheme
                {
                    Name = "X-Api-Key",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
                };
                o.AddSecurityDefinition("ApiKey", scheme);
                o.AddSecurityRequirement(new OpenApiSecurityRequirement { { scheme, [] } });
            });

            var app = builder.Build();
#if DEBUG
            app.UseSwagger();
            app.UseSwaggerUI();
#else
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
#endif
            app.UseWhen(
                ctx => !ctx.Request.Path.StartsWithSegments("/swagger"),
                branch =>
                {
                    branch.UseMiddleware<RequestLoggingMiddleware>();
                    branch.UseMiddleware<IpWhitelistMiddleware>();
                    branch.UseMiddleware<ApiKeyMiddleware>();
                });
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
