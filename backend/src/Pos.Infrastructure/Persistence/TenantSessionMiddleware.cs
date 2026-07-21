using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Wraps every authenticated request's database work in one transaction and issues
/// <c>SET LOCAL app.tenant_id</c> as its first statement. SET LOCAL is scoped to the
/// enclosing transaction and is discarded by Postgres at COMMIT/ROLLBACK, so a pooled
/// connection handed to a different tenant's request afterward can never inherit this
/// value — there is no manual reset to get wrong.
/// </summary>
public sealed class TenantSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PosDbContext dbContext, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var tenantId = tenantContext.TenantId;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(context.RequestAborted);
        try
        {
            // Postgres's SET command grammar does not accept a bound parameter for the
            // config value, so ExecuteSqlInterpolated/ExecuteSqlAsync (which parameterize)
            // cannot be used here — ExecuteSqlRaw is the only option. Guid.ToString("D")
            // produces a fixed hex-and-dash literal that can never carry SQL metacharacters,
            // so this raw interpolation is safe; verified, not merely assumed.
#pragma warning disable EF1002
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SET LOCAL app.tenant_id = '{tenantId:D}'",
                context.RequestAborted);
#pragma warning restore EF1002

            await next(context);

            await transaction.CommitAsync(context.RequestAborted);
        }
        catch
        {
            await transaction.RollbackAsync(context.RequestAborted);
            throw;
        }
    }
}
