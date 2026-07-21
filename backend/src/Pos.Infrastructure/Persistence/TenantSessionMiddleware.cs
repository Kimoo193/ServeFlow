using Microsoft.AspNetCore.Http;
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
        // SignalR hub connections are long-lived: next(context) below would not return
        // until the connection closes, which would hold this transaction (and its
        // pooled connection) open for the connection's entire lifetime — exhausting the
        // pool as soon as more than a handful of KDS/waiter clients stay connected.
        // PosHub methods call TenantSessionSql.SetTenantAsync themselves, per invocation,
        // instead of relying on this middleware.
        if (context.User.Identity?.IsAuthenticated != true || context.Request.Path.StartsWithSegments("/posHub"))
        {
            await next(context);
            return;
        }

        var tenantId = tenantContext.TenantId;

        // No explicit try/catch/rollback: `await using` disposes an uncommitted
        // transaction on any exit path (including an exception from next(context)),
        // and EF Core/Npgsql roll it back automatically on disposal. An earlier version
        // added an explicit catch { RollbackAsync(context.RequestAborted); throw; } —
        // that was not just redundant, it was actively wrong: reusing the same
        // (possibly already-canceled) RequestAborted token for the rollback meant a
        // client disconnect could make RollbackAsync itself throw OperationCanceledException,
        // discarding whatever the real exception was before `throw;` ever ran.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(context.RequestAborted);

        await dbContext.Database.SetTenantAsync(tenantId, context.RequestAborted);

        await next(context);

        await transaction.CommitAsync(context.RequestAborted);
    }
}
