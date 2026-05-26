using System.Net;
using GameServer.Auth;
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

            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Any, port));
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddControllers();
            builder.Services.AddSingleton<SmtpService>();
            builder.Services.AddCors(o => o.AddPolicy("AuthCors", p =>
                p.AllowAnyOrigin().WithMethods("POST").WithHeaders("Content-Type")));
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
            app.UseCors("AuthCors");

            // 로깅은 /swagger, /health 제외하고 전체 적용
            app.UseWhen(
                ctx => !ctx.Request.Path.StartsWithSegments("/swagger")
                    && !ctx.Request.Path.StartsWithSegments("/health"),
                branch => branch.UseMiddleware<RequestLoggingMiddleware>());

            // IP 화이트리스트 + API 키는 /auth/ 제외 (비밀번호 재설정은 공개 엔드포인트)
            app.UseWhen(
                ctx => !ctx.Request.Path.StartsWithSegments("/swagger")
                    && !ctx.Request.Path.StartsWithSegments("/health")
                    && !ctx.Request.Path.StartsWithSegments("/auth"),
                branch =>
                {
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
