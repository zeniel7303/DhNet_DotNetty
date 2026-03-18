using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GameServer.Web.Middleware;

/// <summary>
/// 모든 Web API 요청에 X-Api-Key 헤더 인증을 적용하는 미들웨어.
/// appsettings.json의 AdminApi:ApiKey 값과 비교한다.
/// </summary>
public class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    private const string HeaderName = "X-Api-Key";
    private readonly string _apiKey = config["AdminApi:ApiKey"]
        ?? throw new InvalidOperationException("appsettings.json에 'AdminApi:ApiKey'가 설정되지 않았습니다.");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var provided)
            || provided != _apiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await next(context);
    }
}
