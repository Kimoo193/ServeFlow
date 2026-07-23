using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class PermissionAuthorization
{
    public static async Task<bool> HasPermissionAsync(PosDbContext db, ITenantContext ctx, Permission p, CancellationToken ct)
    {
        return ctx.Role == Role.Owner
            || await db.RolePermissions.AnyAsync(rp => rp.Role == ctx.Role && rp.Permission == p, ct);
    }

    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder b, Permission p)
    {
        return b.AddEndpointFilter(async (context, next) =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<PosDbContext>();
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();

            if (!await HasPermissionAsync(db, tenantContext, p, context.HttpContext.RequestAborted))
            {
                return Results.Problem(
                    statusCode: 403,
                    title: "Insufficient permissions",
                    detail: $"Requires the {p} permission.");
            }

            return await next(context);
        });
    }
}
