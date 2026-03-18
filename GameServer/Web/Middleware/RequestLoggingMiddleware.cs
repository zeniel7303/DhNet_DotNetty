using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace GameServer.Web.Middleware;

/// <summary>
/// 모든 Web API 요청/응답을 GameLogger로 기록하는 미들웨어.
/// 메서드, 경로, 쿼리, 클라이언트 IP, 요청 Body, 응답 상태 코드, 응답 Body, 처리 시간을 로깅한다.
/// </summary>
public class RequestLoggingMiddleware(RequestDelegate next)
{
    private const string Tag = "WebAPI";
    private const int MaxBodyLogLength = 1024; // 1KB 초과 시 truncate

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var sw = Stopwatch.StartNew();

        var path = request.Path.Value ?? "/";
        var query = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var reqBody = await ReadRequestBodyAsync(request);
        var reqBodyLog = string.IsNullOrEmpty(reqBody) ? string.Empty : $"\n  Body: {reqBody}";

        Common.Logging.GameLogger.Info(Tag,
            $"→ {request.Method} {path}{query} from {clientIp}{reqBodyLog}");

        // Response.Body는 write-only이므로 MemoryStream으로 교체해서 읽은 뒤 원본에 복사
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            sw.Stop();
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
            context.Response.Body = originalBody;

            Common.Logging.GameLogger.Error(Tag,
                $"← {request.Method} {path} 500 ({sw.ElapsedMilliseconds}ms) [unhandled exception]", ex);
            throw;
        }

        sw.Stop();

        var respBody = await ReadResponseBodyAsync(context.Response, buffer);
        var respBodyLog = string.IsNullOrEmpty(respBody) ? string.Empty : $"\n  Body: {respBody}";

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
        context.Response.Body = originalBody;

        var status = context.Response.StatusCode;
        if (status >= 500)
            Common.Logging.GameLogger.Error(Tag,
                $"← {request.Method} {path} {status} ({sw.ElapsedMilliseconds}ms){respBodyLog}");
        else if (status >= 400)
            Common.Logging.GameLogger.Warn(Tag,
                $"← {request.Method} {path} {status} ({sw.ElapsedMilliseconds}ms){respBodyLog}");
        else
            Common.Logging.GameLogger.Info(Tag,
                $"← {request.Method} {path} {status} ({sw.ElapsedMilliseconds}ms){respBodyLog}");
    }

    private async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (!request.HasJsonContentType() &&
            request.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) != true)
        {
            return string.Empty;
        }

        if ((request.ContentLength ?? 0) == 0)
        {
            return string.Empty;
        }

        request.EnableBuffering();

        using var reader = new StreamReader(
            request.Body,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var raw = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (raw.Length > MaxBodyLogLength)
        {
            return raw[..MaxBodyLogLength] + $"... (truncated, total {raw.Length} chars)";
        }

        return raw;
    }

    private async Task<string> ReadResponseBodyAsync(HttpResponse response, MemoryStream buffer)
    {
        var contentType = response.ContentType;
        if (contentType is null)
        {
            return string.Empty;
        }

        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
            !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
        var raw = await reader.ReadToEndAsync();

        if (raw.Length > MaxBodyLogLength)
        {
            return raw[..MaxBodyLogLength] + $"... (truncated, total {raw.Length} chars)";
        }

        return raw;
    }
}
