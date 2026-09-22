using GameServer.Database;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DBServer;

/// <summary>게임 DB와 로그 DB 연결을 확인한다.</summary>
public sealed class MysqlHealthCheck(DatabaseSystem database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var ok = await database.CheckHealthAsync();
        return ok
            ? HealthCheckResult.Healthy("MySQL 연결됨")
            : HealthCheckResult.Unhealthy("MySQL 연결 실패");
    }
}
