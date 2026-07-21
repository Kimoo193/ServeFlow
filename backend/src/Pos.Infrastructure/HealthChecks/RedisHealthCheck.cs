using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Pos.Infrastructure.HealthChecks;

public sealed class RedisHealthCheck(Lazy<Task<IConnectionMultiplexer>> connectionMultiplexerTask) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionMultiplexer = await connectionMultiplexerTask.Value;
        var latency = await connectionMultiplexer.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy($"Redis responded in {latency.TotalMilliseconds:F0} ms.");
    }
}
