using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class DashboardEndpoints
{
    public static WebApplication MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        group.MapGet("/dashboard/summary", async (PosDbContext db, IClock clock, CancellationToken ct) =>
        {
            var todayStart = clock.UtcNow.Date;

            var ordersToday = await db.OrderEvents
                .Where(oe => oe.EventType == OrderEventType.Created && oe.PerformedAt >= todayStart)
                .Select(oe => oe.OrderId)
                .Distinct()
                .CountAsync(ct);

            var openNow = await db.Orders
                .Where(o => o.Status == OrderStatus.Open || o.Status == OrderStatus.InProgress)
                .CountAsync(ct);

            return Results.Ok(new
            {
                date = clock.UtcNow.ToString("yyyy-MM-dd"),
                ordersToday,
                openNow,
            });
        });

        return app;
    }
}
