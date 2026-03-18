using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GameServer.Web.Middleware;

/// <summary>
/// IP 화이트리스트 미들웨어. AdminApi:AllowedIps 목록에 없는 IP는 403을 반환한다.
/// 목록이 비어있으면 모든 IP를 허용한다 (화이트리스트 미등록 상태).
/// </summary>
public class IpWhitelistMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly HashSet<IPAddress> _allowedIps = config
        .GetSection("AdminApi:AllowedIps")
        .Get<string[]>()
        ?.Select(IPAddress.Parse)
        .ToHashSet()
        ?? [];

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowedIps.Count > 0)
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is null || !_allowedIps.Contains(remoteIp.MapToIPv4()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
                return;
            }
        }

        await next(context);
    }
}
