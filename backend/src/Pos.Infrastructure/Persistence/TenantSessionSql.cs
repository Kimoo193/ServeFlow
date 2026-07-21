using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Shared by TenantSessionMiddleware and DevSeeder — both need to issue
/// <c>SET LOCAL app.tenant_id</c> as the first statement of their own transaction.
/// </summary>
internal static class TenantSessionSql
{
    public static async Task SetTenantAsync(this DatabaseFacade database, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Postgres's SET command grammar does not accept a bound parameter for the
        // config value, so ExecuteSqlInterpolated/ExecuteSqlAsync (which parameterize)
        // cannot be used here — ExecuteSqlRaw is the only option. Guid.ToString("D")
        // produces a fixed hex-and-dash literal that can never carry SQL metacharacters,
        // so this raw interpolation is safe; verified, not merely assumed.
#pragma warning disable EF1002
        await database.ExecuteSqlRawAsync($"SET LOCAL app.tenant_id = '{tenantId:D}'", cancellationToken);
#pragma warning restore EF1002
    }
}
