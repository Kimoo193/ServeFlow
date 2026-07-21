using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Pos.Infrastructure.HealthChecks;

public sealed class RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var latency = await connectionMultiplexer.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy($"Redis responded in {latency.TotalMilliseconds:F0} ms.");
    }
}
