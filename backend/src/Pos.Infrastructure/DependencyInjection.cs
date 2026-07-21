using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pos.Application.Common;
using Pos.Infrastructure.Auth;
using Pos.Infrastructure.HealthChecks;
using Pos.Infrastructure.Persistence;
using StackExchange.Redis;

namespace Pos.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // Both registrations below resolve IConfiguration lazily via the service
        // provider at construction time, rather than capturing configuration.Get...()
        // as a closed-over local here. A local capture at registration time can miss
        // configuration overrides applied later in the host-building pipeline (found by
        // an integration test under WebApplicationFactory: the app connected to the
        // original appsettings connection string instead of the test's override, even
        // though the override was visible everywhere else).
        services.AddDbContext<PosDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");
            options.UseNpgsql(connectionString);
        });

        services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Connection string 'Redis' is not configured.");

            var redisOptions = ConfigurationOptions.Parse(connectionString);
            redisOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(redisOptions);
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, JwtTenantContext>();

        services
            .AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RedisHealthCheck>("redis");

        return services;
    }
}
