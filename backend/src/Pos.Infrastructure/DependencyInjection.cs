using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // Lazy<Task<T>> instead of a plain IConnectionMultiplexer singleton: connecting
        // is deferred to first use (only RedisHealthCheck triggers it today) and done via
        // ConnectAsync, so whichever request first resolves this yields its thread during
        // the handshake instead of blocking it — Connect() (synchronous) would block.
        // Lazy<T>'s default thread-safety mode still runs the factory exactly once even
        // if concurrent health checks race for it.
        services.AddSingleton(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Connection string 'Redis' is not configured.");

            return new Lazy<Task<IConnectionMultiplexer>>(async () =>
            {
                var redisOptions = ConfigurationOptions.Parse(connectionString);
                redisOptions.AbortOnConnectFail = false;
                return await ConnectionMultiplexer.ConnectAsync(redisOptions);
            });
        });

        services.AddSingleton<IClock, SystemClock>();

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, JwtTenantContext>();

        services
            .AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres")
            .AddCheck<RedisHealthCheck>("redis");

        // The string overload of AddStackExchangeRedis needs a connection string
        // synchronously at registration time, which reintroduces the early-capture bug
        // this file otherwise avoids (confirmed by a failing test: WebApplicationFactory
        // adds its ConfigureAppConfiguration overrides to IConfiguration during Build(),
        // strictly after this method runs, so any read here — direct or via a throwaway
        // BuildServiceProvider() — sees an incomplete configuration).
        // AddStackExchangeRedis() (no args) instead wires the Redis backplane to whatever
        // IOptions<RedisOptions> DI resolves, which — like AddDbContext/AddSingleton above —
        // is only actually constructed post-Build(), when SignalR first needs it.
        services.AddOptions<RedisOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var connectionString = configuration.GetConnectionString("Redis")
                ?? throw new InvalidOperationException("Connection string 'Redis' is not configured.");
            options.Configuration = ConfigurationOptions.Parse(connectionString);
        });

        services.AddSignalR()
            .AddStackExchangeRedis();

        return services;
    }
}
