using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class OrderEndpoints
{
    public static WebApplication MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        // --- Open a dine-in order on a table ---
        group.MapPost("/tables/{tableId:guid}/orders", async (Guid tableId, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var table = await db.DiningTables.FindAsync([tableId], ct);
            if (table is null) return Results.NotFound();

            var order = new Order
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                BranchId = tenantContext.BranchId,
                TableId = tableId,
                WaiterId = tenantContext.UserId,
                OrderType = OrderType.DineIn,
                Status = OrderStatus.Open,
                RowVersion = 1,
            };
            db.Orders.Add(order);

            // specs/03-state-machines.md: "Available --> Occupied : order opened on table".
            // The partial unique index on orders(table_id) is the actual enforcement
            // mechanism (see OrderConfiguration); this keeps the displayed status in sync
            // with it so GET /tables reflects reality for "which tables are free".
            table.Status = TableStatus.Occupied;

            await db.SaveChangesAsync(ct);
            // UniqueViolationExceptionHandler (registered in Program.cs) catches the
            // partial-unique-index violation from a concurrent second order on this table
            // and returns 409 — no local catch needed here.

            return Results.Created($"/orders/{order.Id}", new { orderId = order.Id, status = order.Status.ToString(), rowVersion = order.RowVersion });
        });

        // --- Open a takeaway order ---
        group.MapPost("/orders", async (CreateTakeawayOrderRequest? body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var dailyNo = await db.Orders
                .Where(o => o.TenantId == tenantContext.TenantId && o.BranchId == tenantContext.BranchId && o.OrderType == OrderType.Takeaway)
                .CountAsync(ct) + 1;

            var order = new Order
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                BranchId = tenantContext.BranchId,
                WaiterId = tenantContext.UserId,
                OrderType = OrderType.Takeaway,
                Status = OrderStatus.Open,
                DailyNo = dailyNo,
                CustomerName = body?.CustomerName,
                RowVersion = 1,
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/orders/{order.Id}", new { orderId = order.Id, status = order.Status.ToString(), dailyNo = order.DailyNo, rowVersion = order.RowVersion });
        });

        // --- Add a draft line ---
        group.MapPost("/orders/{orderId:guid}/lines", async (Guid orderId, AddDraftLineRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Open)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var menuItem = await db.MenuItems.FindAsync([body.MenuItemId], ct);
            if (menuItem is null) return Results.Problem(statusCode: 400, title: "Menu item not found");
            if (menuItem.Availability != Availability.Available)
                return Results.Problem(statusCode: 400, title: "Item not available", detail: "Item is OutOfStock or Hidden");

            var maxSequence = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .MaxAsync(ol => (int?)ol.Sequence, ct) ?? 0;

            var line = new OrderLine
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                ItemId = body.MenuItemId,
                StationId = menuItem.StationId,
                Sequence = maxSequence + 1,
                Quantity = body.Quantity,
                UnitPrice = menuItem.Price,
                Status = OrderLineStatus.Draft,
            };
            db.OrderLines.Add(line);

            if (body.ModifierIds is { Count: > 0 })
            {
                var modifiers = await db.Modifiers
                    .Where(m => body.ModifierIds.Contains(m.Id))
                    .ToListAsync(ct);

                foreach (var modifier in modifiers)
                {
                    db.OrderLineModifiers.Add(new OrderLineModifier
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantContext.TenantId,
                        OrderLineId = line.Id,
                        Name = modifier.Name,
                        PriceDelta = modifier.PriceDelta,
                    });
                }
            }

            order.RowVersion++;
            await db.SaveChangesAsync(ct);

            return Results.Created($"/orders/{orderId}/lines/{line.Id}", new { lineId = line.Id, sequence = line.Sequence, unitPrice = line.UnitPrice, rowVersion = order.RowVersion });
        });

        // --- Update a draft line ---
        group.MapPatch("/orders/{orderId:guid}/lines/{lineId:guid}", async (Guid orderId, Guid lineId, UpdateDraftLineRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Open)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Draft)
                return Results.Problem(statusCode: 404, title: "Line is not Draft");

            var menuItem = await db.MenuItems.FindAsync([line.ItemId], ct);
            if (menuItem is null) return Results.Problem(statusCode: 400, title: "Menu item not found");

            line.Quantity = body.Quantity;
            line.UnitPrice = menuItem.Price;

            // Replace modifiers: delete existing, insert new
            var existingModifiers = await db.OrderLineModifiers
                .Where(olm => olm.OrderLineId == lineId)
                .ToListAsync(ct);
            db.OrderLineModifiers.RemoveRange(existingModifiers);

            if (body.ModifierIds is { Count: > 0 })
            {
                var modifiers = await db.Modifiers
                    .Where(m => body.ModifierIds.Contains(m.Id))
                    .ToListAsync(ct);

                foreach (var modifier in modifiers)
                {
                    db.OrderLineModifiers.Add(new OrderLineModifier
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantContext.TenantId,
                        OrderLineId = lineId,
                        Name = modifier.Name,
                        PriceDelta = modifier.PriceDelta,
                    });
                }
            }

            order.RowVersion++;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { lineId = line.Id, sequence = line.Sequence, quantity = line.Quantity, unitPrice = line.UnitPrice, rowVersion = order.RowVersion });
        });

        // --- Remove (void) a draft line ---
        group.MapDelete("/orders/{orderId:guid}/lines/{lineId:guid}", async (Guid orderId, Guid lineId, int rowVersion, PosDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            if (order.RowVersion != rowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Draft)
                return Results.Problem(statusCode: 404, title: "Line is not Draft");

            line.Status = OrderLineStatus.Voided;

            order.RowVersion++;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { rowVersion = order.RowVersion });
        });

        // --- Order tracking (read) ---
        group.MapGet("/orders/{orderId:guid}", async (Guid orderId, PosDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            var lines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .OrderBy(ol => ol.Sequence)
                .ToListAsync(ct);

            var lineIds = lines.Select(l => l.Id).ToHashSet();
            var modifiers = lineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => lineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var linesDto = lines.Select(l => new
            {
                id = l.Id,
                itemId = l.ItemId,
                stationId = l.StationId,
                sequence = l.Sequence,
                quantity = l.Quantity,
                unitPrice = l.UnitPrice,
                status = l.Status.ToString(),
                modifiers = modifiers
                    .Where(m => m.OrderLineId == l.Id)
                    .Select(m => new { name = m.Name, priceDelta = m.PriceDelta }),
            });

            return Results.Ok(new
            {
                id = order.Id,
                tableId = order.TableId,
                waiterId = order.WaiterId,
                orderType = order.OrderType.ToString(),
                status = order.Status.ToString(),
                dailyNo = order.DailyNo,
                customerName = order.CustomerName,
                rowVersion = order.RowVersion,
                lines = linesDto,
            });
        });

        return app;
    }
}

internal sealed record CreateTakeawayOrderRequest(string? CustomerName);
internal sealed record AddDraftLineRequest(Guid MenuItemId, int Quantity, List<Guid> ModifierIds, int RowVersion);
internal sealed record UpdateDraftLineRequest(int Quantity, List<Guid> ModifierIds, int RowVersion);
