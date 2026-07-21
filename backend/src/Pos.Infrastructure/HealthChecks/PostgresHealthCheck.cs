using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.HealthChecks;

public sealed class PostgresHealthCheck(PosDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
            : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
    }
}
