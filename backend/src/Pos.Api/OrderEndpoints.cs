using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Hubs;
using Pos.Application.Common;
using Pos.Domain.Combos;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class OrderEndpoints
{
    public static WebApplication MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        // --- Open a dine-in order on a table ---
        group.MapPost("/tables/{tableId:guid}/orders", async (Guid tableId, CreateDineInOrderRequest? body, PosDbContext db, ITenantContext tenantContext, IClock clock, CancellationToken ct) =>
        {
            // Idempotent replay: if a client-supplied OrderId already exists, return the
            // existing order without any side effects (no new insert, no table status change).
            if (body?.OrderId.HasValue == true)
            {
                var existing = await db.Orders.FindAsync([body.OrderId.Value], ct);
                if (existing is not null)
                {
                    if (existing.TableId == tableId && existing.OrderType == OrderType.DineIn)
                        return Results.Created($"/orders/{existing.Id}", new { orderId = existing.Id, status = existing.Status.ToString(), rowVersion = existing.RowVersion });
                    return Results.Problem(statusCode: 409, title: "Order id conflict", detail: "That order id belongs to a different order");
                }
            }

            var table = await db.DiningTables.FindAsync([tableId], ct);
            if (table is null) return Results.NotFound();

            var order = new Order
            {
                Id = body?.OrderId ?? Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                BranchId = tenantContext.BranchId,
                TableId = tableId,
                WaiterId = tenantContext.UserId,
                OrderType = OrderType.DineIn,
                Status = OrderStatus.Open,
                RowVersion = 1,
            };
            db.Orders.Add(order);

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = order.Id,
                EventType = OrderEventType.Created,
                Details = JsonSerializer.Serialize(new { orderType = order.OrderType.ToString(), tableId }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

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
        }).RequirePermission(Permission.TakeOrder);

        // --- Open a takeaway order ---
        group.MapPost("/orders", async (CreateTakeawayOrderRequest? body, PosDbContext db, ITenantContext tenantContext, IClock clock, CancellationToken ct) =>
        {
            // Idempotent replay: if a client-supplied OrderId already exists, return the
            // existing order's current state (do NOT re-run the dailyNo COUNT query).
            if (body?.OrderId.HasValue == true)
            {
                var existing = await db.Orders.FindAsync([body.OrderId.Value], ct);
                if (existing is not null)
                {
                    return Results.Created($"/orders/{existing.Id}", new { orderId = existing.Id, status = existing.Status.ToString(), dailyNo = existing.DailyNo, rowVersion = existing.RowVersion });
                }
            }

            var dailyNo = await db.Orders
                .Where(o => o.TenantId == tenantContext.TenantId && o.BranchId == tenantContext.BranchId && o.OrderType == OrderType.Takeaway)
                .CountAsync(ct) + 1;

            var order = new Order
            {
                Id = body?.OrderId ?? Guid.NewGuid(),
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

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = order.Id,
                EventType = OrderEventType.Created,
                Details = JsonSerializer.Serialize(new { orderType = order.OrderType.ToString(), customerName = order.CustomerName }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            return Results.Created($"/orders/{order.Id}", new { orderId = order.Id, status = order.Status.ToString(), dailyNo = order.DailyNo, rowVersion = order.RowVersion });
        }).RequirePermission(Permission.TakeOrder);

        // --- Add a draft line ---
        group.MapPost("/orders/{orderId:guid}/lines", async (Guid orderId, AddDraftLineRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            // Idempotent replay: if a client-supplied LineId already exists for this order,
            // return the existing line's current state (no row_version check, no new insert).
            if (body.LineId.HasValue)
            {
                var existingLine = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == body.LineId.Value && ol.OrderId == orderId, ct);
                if (existingLine is not null)
                {
                    var existingOrder = await db.Orders.FindAsync([orderId], ct);
                    return Results.Created($"/orders/{orderId}/lines/{existingLine.Id}", new { lineId = existingLine.Id, sequence = existingLine.Sequence, unitPrice = existingLine.UnitPrice, rowVersion = existingOrder?.RowVersion ?? 0 });
                }
            }

            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (!order.IsAcceptingChanges)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            if (body.Quantity is < 1 or > 999)
                return Results.Problem(statusCode: 400, title: "Invalid quantity", detail: "Quantity must be between 1 and 999.");

            var menuItem = await db.MenuItems.FindAsync([body.MenuItemId], ct);
            if (menuItem is null) return Results.Problem(statusCode: 400, title: "Menu item not found");
            if (menuItem.Availability != Availability.Available)
                return Results.Problem(statusCode: 400, title: "Item not available", detail: "Item is OutOfStock or Hidden");

            var maxSequence = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .MaxAsync(ol => (int?)ol.Sequence, ct) ?? 0;

            var line = new OrderLine
            {
                Id = body.LineId ?? Guid.NewGuid(),
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
        }).RequirePermission(Permission.TakeOrder);

        // --- Add a combo: expands into one Draft line per component unit (BR-O-06) ---
        group.MapPost("/orders/{orderId:guid}/combos", async (Guid orderId, AddComboRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            // Idempotent replay, checked before anything else exactly as add-line does: if this
            // combo instance already expanded onto this order, hand back what is there rather
            // than expanding it twice. No row_version check, no insert.
            if (body.ComboInstanceId.HasValue)
            {
                var alreadyExpanded = await db.OrderLines
                    .Where(ol => ol.OrderId == orderId && ol.ComboInstanceId == body.ComboInstanceId.Value)
                    .OrderBy(ol => ol.Sequence)
                    .ToListAsync(ct);

                if (alreadyExpanded.Count > 0)
                {
                    var replayedOrder = await db.Orders.FindAsync([orderId], ct);
                    return Results.Created(
                        $"/orders/{orderId}",
                        ComboExpansionResponse(alreadyExpanded, replayedOrder?.RowVersion ?? 0));
                }
            }

            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (!order.IsAcceptingChanges)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var combo = await db.Combos.FindAsync([body.ComboId], ct);
            if (combo is null) return Results.Problem(statusCode: 400, title: "Combo not found");

            var comboItems = await db.ComboItems
                .Where(ci => ci.ComboId == combo.Id)
                .ToListAsync(ct);

            if (comboItems.Count == 0)
                return Results.Problem(
                    statusCode: 400,
                    title: "Combo has no components",
                    detail: "A combo with no components cannot be ordered.");

            if (comboItems.Any(ci => ci.Quantity < 1))
                return Results.Problem(
                    statusCode: 400,
                    title: "Combo component quantity is invalid",
                    detail: "Every component must have a quantity of at least 1.");

            var componentIds = comboItems.Select(ci => ci.ItemId).ToList();
            var menuItems = await db.MenuItems
                .Where(mi => componentIds.Contains(mi.Id))
                .ToDictionaryAsync(mi => mi.Id, ct);

            // One unavailable component makes the whole combo unorderable — the customer cannot
            // be sold most of a bundle at the bundle price.
            foreach (var comboItem in comboItems)
            {
                if (!menuItems.TryGetValue(comboItem.ItemId, out var component))
                    return Results.Problem(statusCode: 400, title: "Menu item not found");

                if (component.Availability != Availability.Available)
                    return Results.Problem(
                        statusCode: 400,
                        title: "Item not available",
                        detail: $"'{component.Name}' is OutOfStock or Hidden.");
            }

            var totalUnits = comboItems.Sum(ci => ci.Quantity);
            if (totalUnits > 50)
                return Results.Problem(
                    statusCode: 400,
                    title: "Combo too large",
                    detail: $"A combo may not expand into more than 50 lines. This combo would produce {totalUnits}.");

            // One quantity-1 line per unit: see ComboAllocation for why quantity cannot be > 1.
            var units = comboItems
                .OrderBy(ci => menuItems[ci.ItemId].Name)
                .ThenBy(ci => ci.ItemId)
                .SelectMany(ci => Enumerable.Repeat(menuItems[ci.ItemId], ci.Quantity))
                .ToList();

            var allocated = ComboAllocation.Allocate(combo.Price, units.Select(u => u.Price).ToList());

            var maxSequence = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .MaxAsync(ol => (int?)ol.Sequence, ct) ?? 0;

            var comboInstanceId = body.ComboInstanceId ?? Guid.NewGuid();

            var lines = new List<OrderLine>(units.Count);
            for (var i = 0; i < units.Count; i++)
            {
                var line = new OrderLine
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantContext.TenantId,
                    OrderId = orderId,
                    ItemId = units[i].Id,
                    StationId = units[i].StationId,
                    Sequence = maxSequence + 1 + i,
                    Quantity = 1,
                    UnitPrice = allocated[i],
                    Status = OrderLineStatus.Draft,
                    ComboId = combo.Id,
                    ComboInstanceId = comboInstanceId,
                    ComboName = combo.Name,
                };
                lines.Add(line);
                db.OrderLines.Add(line);
            }

            order.RowVersion++;
            await db.SaveChangesAsync(ct);

            return Results.Created($"/orders/{orderId}", ComboExpansionResponse(lines, order.RowVersion));
        }).RequirePermission(Permission.TakeOrder);

        // --- Update a draft line ---
        group.MapPatch("/orders/{orderId:guid}/lines/{lineId:guid}", async (Guid orderId, Guid lineId, UpdateDraftLineRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (!order.IsAcceptingChanges)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Draft)
                return Results.Problem(statusCode: 404, title: "Line is not Draft");

            // A combo component carries an allocated share of a bundle price, not its own list
            // price. Editing it here would reset UnitPrice to the menu price below and quietly
            // break the bundle total, so remove the combo and re-add it instead.
            if (line.ComboInstanceId is not null)
                return Results.Problem(
                    statusCode: 409,
                    title: "Combo component cannot be edited",
                    detail: "This line is part of a combo. Remove the combo and add it again.");

            var menuItem = await db.MenuItems.FindAsync([line.ItemId], ct);
            if (menuItem is null) return Results.Problem(statusCode: 400, title: "Menu item not found");

            if (body.Quantity is < 1 or > 999)
                return Results.Problem(statusCode: 400, title: "Invalid quantity", detail: "Quantity must be between 1 and 999.");

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
        }).RequirePermission(Permission.TakeOrder);

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
        }).RequirePermission(Permission.TakeOrder);

        // --- Open orders (cashier list) ---
        group.MapGet("/orders/open", async (PosDbContext db, CancellationToken ct) =>
        {
            var orders = await db.Orders
                .Where(Order.AcceptingChanges)
                .OrderBy(o => o.DailyNo)
                .ToListAsync(ct);

            if (orders.Count == 0)
                return Results.Ok(Array.Empty<object>());

            var orderIds = orders.Select(o => o.Id).ToHashSet();
            var tableIds = orders.Where(o => o.TableId.HasValue).Select(o => o.TableId!.Value).ToHashSet();

            var tables = tableIds.Count > 0
                ? await db.DiningTables.Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Label, ct)
                : [];

            var lineStats = await db.OrderLines
                .Where(ol => orderIds.Contains(ol.OrderId))
                .GroupBy(ol => ol.OrderId)
                .Select(g => new
                {
                    OrderId = g.Key,
                    LineCount = g.Count(l => l.Status != OrderLineStatus.Voided),
                    UnfinishedCount = g.Count(l => l.Status != OrderLineStatus.Served && l.Status != OrderLineStatus.Voided),
                })
                .ToListAsync(ct);

            var statsMap = lineStats.ToDictionary(s => s.OrderId, s => (LineCount: s.LineCount, UnfinishedCount: s.UnfinishedCount));

            var result = orders.Select(o =>
            {
                tables.TryGetValue(o.TableId ?? Guid.Empty, out var label);
                var stats = statsMap.GetValueOrDefault(o.Id);
                return new
                {
                    id = o.Id,
                    tableId = o.TableId,
                    tableLabel = o.TableId.HasValue ? label : null,
                    orderType = o.OrderType.ToString(),
                    status = o.Status.ToString(),
                    dailyNo = o.DailyNo,
                    customerName = o.CustomerName,
                    lineCount = stats.LineCount,
                    unfinishedCount = stats.UnfinishedCount,
                    rowVersion = o.RowVersion,
                };
            });

            return Results.Ok(result);
        }).RequireAnyPermission(Permission.CloseOrder, Permission.TakeOrder);

        // --- Order history (read-only manager view) ---
        group.MapGet("/orders/history", async (int? limit, PosDbContext db, CancellationToken ct) =>
        {
            var actualLimit = Math.Clamp(limit ?? 100, 1, 500);

            var recentCreated = await db.OrderEvents
                .Where(oe => oe.EventType == OrderEventType.Created)
                .OrderByDescending(oe => oe.PerformedAt)
                .Take(actualLimit)
                .Select(oe => new { oe.OrderId, oe.PerformedAt })
                .ToListAsync(ct);

            if (recentCreated.Count == 0)
                return Results.Ok(Array.Empty<object>());

            var orderIds = recentCreated.Select(r => r.OrderId).ToHashSet();

            var orders = await db.Orders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync(ct);

            var tableIds = orders.Where(o => o.TableId.HasValue).Select(o => o.TableId!.Value).ToHashSet();
            var tables = tableIds.Count > 0
                ? await db.DiningTables.Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Label, ct)
                : [];

            var completedEvents = await db.OrderEvents
                .Where(oe => oe.EventType == OrderEventType.Completed && orderIds.Contains(oe.OrderId))
                .GroupBy(oe => oe.OrderId)
                .Select(g => new { OrderId = g.Key, PerformedAt = g.Min(oe => oe.PerformedAt) })
                .ToListAsync(ct);

            var lineCounts = await db.OrderLines
                .Where(ol => orderIds.Contains(ol.OrderId) && ol.Status != OrderLineStatus.Voided)
                .GroupBy(ol => ol.OrderId)
                .Select(g => new { OrderId = g.Key, LineCount = g.Count() })
                .ToListAsync(ct);

            var createdMap = recentCreated.ToDictionary(r => r.OrderId, r => r.PerformedAt);
            var completedMap = completedEvents.ToDictionary(c => c.OrderId, c => (DateTime?)c.PerformedAt);
            var lineCountMap = lineCounts.ToDictionary(lc => lc.OrderId, lc => lc.LineCount);

            var result = orders
                .OrderByDescending(o => createdMap[o.Id])
                .Select(o =>
                {
                    tables.TryGetValue(o.TableId ?? Guid.Empty, out var label);
                    return new
                    {
                        id = o.Id,
                        tableLabel = o.TableId.HasValue ? label : null,
                        orderType = o.OrderType.ToString(),
                        status = o.Status.ToString(),
                        dailyNo = o.DailyNo,
                        customerName = o.CustomerName,
                        lineCount = lineCountMap.GetValueOrDefault(o.Id, 0),
                        total = o.TotalAmount,
                        createdAt = createdMap[o.Id].ToString("O"),
                        completedAt = completedMap.GetValueOrDefault(o.Id)?.ToString("O"),
                    };
                });

            return Results.Ok(result);
        }).RequirePermission(Permission.ViewHistory);

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
                fireBatchId = l.FireBatchId,
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
                subtotalAmount = order.SubtotalAmount,
                serviceAmount = order.ServiceAmount,
                taxAmount = order.TaxAmount,
                discountAmount = order.DiscountAmount,
                totalAmount = order.TotalAmount,
                rowVersion = order.RowVersion,
                lines = linesDto,
            });
        }).RequireAnyPermission(Permission.TakeOrder, Permission.CloseOrder, Permission.ViewHistory);

        // --- Fire endpoint ---
        group.MapPost("/orders/{orderId:guid}/fire", async (Guid orderId, FireBatchRequest body, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            if (!order.IsAcceptingChanges)
                return Results.Problem(statusCode: 409, title: "Order is not open");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var draftLines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId && ol.Status == OrderLineStatus.Draft)
                .ToListAsync(ct);

            if (draftLines.Count == 0)
                return Results.Problem(statusCode: 409, title: "Nothing to fire");

            var fireBatchId = Guid.NewGuid();
            foreach (var line in draftLines)
            {
                line.FireBatchId = fireBatchId;
                line.Status = OrderLineStatus.Fired;
            }

            if (order.Status == OrderStatus.Open)
                order.Status = OrderStatus.InProgress;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                EventType = OrderEventType.Fired,
                Details = JsonSerializer.Serialize(new
                {
                    fireBatchId,
                    lineIds = draftLines.Select(l => l.Id).ToList(),
                }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            order.RowVersion++;
            await db.SaveChangesAsync(ct);

            // Load modifiers for the fired lines so the broadcast carries them
            var firedLineIds = draftLines.Select(l => l.Id).ToHashSet();
            var firedModifiers = firedLineIds.Count > 0
                ? await db.OrderLineModifiers.Where(m => firedLineIds.Contains(m.OrderLineId)).ToListAsync(ct)
                : [];

            // Broadcast after successful save — fire is authoritative, broadcast is best-effort.
            var groupsByStation = draftLines
                .GroupBy(l => l.StationId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (stationId, stationLines) in groupsByStation)
            {
                await hubContext.Clients.Group($"station:{stationId}").SendAsync("OrderFired", new
                {
                    fireBatchId,
                    orderId,
                    lines = stationLines.Select(l => new
                    {
                        id = l.Id,
                        itemId = l.ItemId,
                        sequence = l.Sequence,
                        quantity = l.Quantity,
                        unitPrice = l.UnitPrice,
                        modifiers = firedModifiers
                            .Where(m => m.OrderLineId == l.Id)
                            .Select(m => new { name = m.Name, priceDelta = m.PriceDelta }),
                    }),
                }, ct);
            }

            return Results.Ok(new
            {
                fireBatchId,
                firedLines = draftLines.Count,
                rowVersion = order.RowVersion,
            });
        }).RequirePermission(Permission.FireOrder);

        // --- KDS read: tickets for a station ---
        group.MapGet("/kds/stations/{stationId:guid}/tickets", async (Guid stationId, PosDbContext db, CancellationToken ct) =>
        {
            var activeLines = await db.OrderLines
                .Where(ol => ol.StationId == stationId && (ol.Status == OrderLineStatus.Fired || ol.Status == OrderLineStatus.Preparing))
                .OrderBy(ol => ol.Sequence)
                .ToListAsync(ct);

            var lineIds = activeLines.Select(l => l.Id).ToHashSet();
            var modifiers = lineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => lineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var tickets = activeLines
                .GroupBy(l => new { l.OrderId, l.StationId, FireBatchId = l.FireBatchId!.Value })
                .OrderBy(g => g.Min(l => l.Sequence)) // oldest first by earliest line sequence
                .Select(g => new
                {
                    orderId = g.Key.OrderId,
                    fireBatchId = g.Key.FireBatchId,
                    lines = g.Select(l => new
                    {
                        id = l.Id,
                        itemId = l.ItemId,
                        sequence = l.Sequence,
                        quantity = l.Quantity,
                        unitPrice = l.UnitPrice,
                        status = l.Status.ToString(),
                        modifiers = modifiers
                            .Where(m => m.OrderLineId == l.Id)
                            .Select(m => new { name = m.Name, priceDelta = m.PriceDelta }),
                    }),
                });

            return Results.Ok(tickets);
            // Gated on BumpKitchenLine: anyone who may bump a line may read the board,
            // avoiding a new ViewKitchen permission that would need seeding and a matrix migration.
            // Kitchen holds only BumpKitchenLine in the seeded matrix — this is the gate that
            // stops a kitchen display reading receipts and bills.
        }).RequirePermission(Permission.BumpKitchenLine);

        // --- KDS: start preparing (Fired -> Preparing) ---
        group.MapPost("/kds/lines/{lineId:guid}/start-preparing", async (Guid lineId, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var line = await db.OrderLines.FindAsync([lineId], ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Fired)
                return Results.Problem(statusCode: 409, title: "Line is not Fired");

            var order = await db.Orders.FindAsync([line.OrderId], ct);

            line.Status = OrderLineStatus.Preparing;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = line.OrderId,
                OrderLineId = line.Id,
                EventType = OrderEventType.StartedPreparing,
                Details = JsonSerializer.Serialize(new { }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            // Broadcast the bump to every KDS terminal on this station so a second
            // screen mirrors it live (the bumping terminal also updates optimistically).
            await hubContext.Clients.Group($"station:{line.StationId}").SendAsync("OrderLineStatusChanged", new
            {
                orderId = line.OrderId,
                lineId = line.Id,
                fireBatchId = line.FireBatchId,
                status = line.Status.ToString(),
            }, ct);

            if (order is not null)
            {
                await hubContext.Clients.Group($"waiter:{order.WaiterId}").SendAsync("LineStatusChanged", new
                {
                    orderId = order.Id,
                    lineId = line.Id,
                    status = line.Status.ToString(),
                    at = clock.UtcNow.ToString("O"),
                }, ct);
            }

            return Results.Ok(new { lineId = line.Id, status = line.Status.ToString(), orderId = line.OrderId });
        }).RequirePermission(Permission.BumpKitchenLine);

        // --- KDS: mark ready (Preparing -> Ready) ---
        group.MapPost("/kds/lines/{lineId:guid}/ready", async (Guid lineId, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var line = await db.OrderLines.FindAsync([lineId], ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Preparing)
                return Results.Problem(statusCode: 409, title: "Line is not Preparing");

            var order = await db.Orders.FindAsync([line.OrderId], ct);

            line.Status = OrderLineStatus.Ready;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = line.OrderId,
                OrderLineId = line.Id,
                EventType = OrderEventType.MarkedReady,
                Details = JsonSerializer.Serialize(new { }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            // Broadcast the bump to every KDS terminal on this station; a Ready line
            // leaves the active board on the receiving screens just as it does here.
            await hubContext.Clients.Group($"station:{line.StationId}").SendAsync("OrderLineStatusChanged", new
            {
                orderId = line.OrderId,
                lineId = line.Id,
                fireBatchId = line.FireBatchId,
                status = line.Status.ToString(),
            }, ct);

            if (order is not null)
            {
                await hubContext.Clients.Group($"waiter:{order.WaiterId}").SendAsync("LineStatusChanged", new
                {
                    orderId = order.Id,
                    lineId = line.Id,
                    status = line.Status.ToString(),
                    at = clock.UtcNow.ToString("O"),
                }, ct);
            }

            return Results.Ok(new { lineId = line.Id, status = line.Status.ToString(), orderId = line.OrderId });
        }).RequirePermission(Permission.BumpKitchenLine);

        // --- Waiter: serve (Ready -> Served) ---
        group.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/serve", async (Guid orderId, Guid lineId, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();
            if (line.Status != OrderLineStatus.Ready)
                return Results.Problem(statusCode: 409, title: "Line is not Ready");

            line.Status = OrderLineStatus.Served;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                OrderLineId = line.Id,
                EventType = OrderEventType.Served,
                Details = JsonSerializer.Serialize(new { }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is not null)
            {
                await hubContext.Clients.Group($"waiter:{order.WaiterId}").SendAsync("LineStatusChanged", new
                {
                    orderId,
                    lineId = line.Id,
                    status = line.Status.ToString(),
                    at = clock.UtcNow.ToString("O"),
                }, ct);
            }

            return Results.Ok(new { lineId = line.Id, status = line.Status.ToString(), orderId });
        }).RequirePermission(Permission.ServeLine);

        // --- Void a line (Fired/Preparing/Ready/Served) ---
        group.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/void", async (Guid orderId, Guid lineId, VoidLineRequest body, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            if (order.Status == OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "Order is already completed", detail: "A completed order's lines cannot be voided");

            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(body.Reason))
                return Results.Problem(statusCode: 400, title: "Reason is required");

            if (line.Status is OrderLineStatus.Draft or OrderLineStatus.Voided)
                return Results.Problem(statusCode: 409, title: "Line cannot be voided in this state");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var hasVoid = await PermissionAuthorization.HasPermissionAsync(db, tenantContext, Permission.VoidLine, ct);
            var isOwnWaiter = tenantContext.Role == Role.Waiter && order.WaiterId == tenantContext.UserId;

            var canVoid = line.Status switch
            {
                OrderLineStatus.Fired => hasVoid || isOwnWaiter,
                OrderLineStatus.Preparing or OrderLineStatus.Ready or OrderLineStatus.Served => hasVoid,
                _ => false,
            };

            if (!canVoid)
                return Results.Problem(statusCode: 403, title: "Insufficient permissions");

            var fromStatus = line.Status;
            var wasteFlag = fromStatus == OrderLineStatus.Preparing;

            line.Status = OrderLineStatus.Voided;
            order.RowVersion++;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                OrderLineId = line.Id,
                EventType = OrderEventType.Voided,
                Reason = body.Reason,
                Details = JsonSerializer.Serialize(new
                {
                    fromStatus = fromStatus.ToString(),
                    wasteFlag,
                }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            // Broadcast after save — best-effort
            await hubContext.Clients.Group($"station:{line.StationId}").SendAsync("OrderLineVoided", new
            {
                orderId,
                lineId,
                itemId = line.ItemId,
                fireBatchId = line.FireBatchId,
            }, ct);

            await hubContext.Clients.Group($"waiter:{order.WaiterId}").SendAsync("LineStatusChanged", new
            {
                orderId,
                lineId,
                status = "Voided",
                at = clock.UtcNow.ToString("O"),
            }, ct);

            return Results.Ok(new { lineId, status = "Voided", rowVersion = order.RowVersion });
        });

        // --- Increase quantity on a Fired/Preparing line ---
        group.MapPost("/orders/{orderId:guid}/lines/{lineId:guid}/increase-quantity", async (Guid orderId, Guid lineId, IncreaseQuantityRequest body, PosDbContext db, ITenantContext tenantContext, IClock clock, IHubContext<PosHub> hubContext, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            var line = await db.OrderLines.FirstOrDefaultAsync(ol => ol.Id == lineId && ol.OrderId == orderId, ct);
            if (line is null) return Results.NotFound();

            if (body.QuantityDelta <= 0)
                return Results.Problem(statusCode: 400, title: "Quantity delta must be positive");

            // The bound applies to the delta, not to line.Quantity + delta: this endpoint does not
            // increment in place, it adds a *new* line whose quantity is the delta (see below).
            // Guarding the sum would refuse a legitimate +1 on a 999 line, whose new line carries
            // quantity 1 and satisfies ck_order_lines_quantity perfectly well.
            if (body.QuantityDelta > 999)
                return Results.Problem(statusCode: 400, title: "Invalid quantity", detail: "Quantity must be between 1 and 999.");

            // The added line would copy this line's allocated bundle share as its price and
            // carry no combo provenance — selling an extra item at the combo discount. Adding
            // another combo is the coherent way to do this.
            if (line.ComboInstanceId is not null)
                return Results.Problem(
                    statusCode: 409,
                    title: "Combo component cannot be increased",
                    detail: "This line is part of a combo. Add another combo instead.");

            if (line.Status is OrderLineStatus.Draft or OrderLineStatus.Ready or OrderLineStatus.Served or OrderLineStatus.Voided)
                return Results.Problem(statusCode: 409, title: "Line cannot be modified in this state");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            var hasVoid = await PermissionAuthorization.HasPermissionAsync(db, tenantContext, Permission.VoidLine, ct);
            var isOwnWaiter = tenantContext.Role == Role.Waiter && order.WaiterId == tenantContext.UserId;

            var canModify = line.Status switch
            {
                OrderLineStatus.Fired => hasVoid || isOwnWaiter,
                OrderLineStatus.Preparing => hasVoid,
                _ => false,
            };

            if (!canModify)
                return Results.Problem(statusCode: 403, title: "Insufficient permissions");

            var menuItem = await db.MenuItems.FindAsync([line.ItemId], ct);
            if (menuItem is null) return Results.Problem(statusCode: 400, title: "Menu item not found");
            if (menuItem.Availability != Availability.Available)
                return Results.Problem(statusCode: 400, title: "Item not available", detail: "Item is OutOfStock or Hidden");

            var maxSequence = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .MaxAsync(ol => (int?)ol.Sequence, ct) ?? 0;

            var newFireBatchId = Guid.NewGuid();
            var newLine = new OrderLine
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                ItemId = line.ItemId,
                StationId = line.StationId,
                Sequence = maxSequence + 1,
                Quantity = body.QuantityDelta,
                // Increasing quantity is "more of the same line" — bill the added units at the
                // original line's snapshotted price, not today's (possibly changed) menu price.
                // The menuItem is still loaded above solely to enforce the availability guard.
                UnitPrice = line.UnitPrice,
                Status = OrderLineStatus.Fired,
                FireBatchId = newFireBatchId,
            };
            db.OrderLines.Add(newLine);

            order.RowVersion++;

            var orderEvent = new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                OrderLineId = line.Id,
                EventType = OrderEventType.QuantityChanged,
                Details = JsonSerializer.Serialize(new
                {
                    originalLineId = line.Id,
                    newLineId = newLine.Id,
                    quantityDelta = body.QuantityDelta,
                    newFireBatchId,
                }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            };
            db.OrderEvents.Add(orderEvent);

            await db.SaveChangesAsync(ct);

            // Broadcast OrderFired to the new line's station group
            await hubContext.Clients.Group($"station:{line.StationId}").SendAsync("OrderFired", new
            {
                fireBatchId = newFireBatchId,
                orderId,
                lines = new[]
                {
                    new
                    {
                        id = newLine.Id,
                        itemId = newLine.ItemId,
                        sequence = newLine.Sequence,
                        quantity = newLine.Quantity,
                        unitPrice = newLine.UnitPrice,
                        modifiers = Array.Empty<object>(), // new line created fresh with no modifiers
                    },
                },
            }, ct);

            return Results.Created($"/orders/{orderId}/lines/{newLine.Id}", new
            {
                lineId = newLine.Id,
                sequence = newLine.Sequence,
                unitPrice = newLine.UnitPrice,
                quantity = newLine.Quantity,
                fireBatchId = newFireBatchId,
                rowVersion = order.RowVersion,
            });
        });

        // --- Close order (snapshot totals, release table) ---
        group.MapPost("/orders/{orderId:guid}/close", async (Guid orderId, CloseOrderRequest body, PosDbContext db, ITenantContext tenantContext, IClock clock, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            if (order.Status == OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "Order is already completed");

            if (order.RowVersion != body.RowVersion)
                return Results.Problem(statusCode: 409, title: "Concurrency conflict", detail: "Stale row version");

            // BR-B-05: block close while any line is not Served or Voided
            var lines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .ToListAsync(ct);

            if (lines.Any(l => l.Status is not (OrderLineStatus.Served or OrderLineStatus.Voided)))
                return Results.Problem(statusCode: 409, title: "Order has unfinished lines", detail: "All lines must be Served or Voided before closing");

            var branchSettings = await db.BranchSettings
                .FirstOrDefaultAsync(bs => bs.BranchId == order.BranchId, ct);

            // Load modifiers for non-voided lines
            var nonVoidedLineIds = lines
                .Where(l => l.Status != OrderLineStatus.Voided)
                .Select(l => l.Id)
                .ToHashSet();

            var modifiers = nonVoidedLineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => nonVoidedLineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var bill = ComputeBill(lines, modifiers, branchSettings);

            // Snapshot onto the order (BR-B-03)
            order.Status = OrderStatus.Completed;
            order.Currency = branchSettings?.Currency;
            order.SubtotalAmount = bill.Subtotal;
            order.ServiceAmount = bill.ServiceAmount;
            order.TaxAmount = bill.TaxAmount;
            order.DiscountAmount = bill.DiscountAmount;
            order.TotalAmount = bill.Total;

            // specs/03-state-machines.md: "Occupied --> Available : order Completed or Cancelled"
            if (order.TableId is not null)
            {
                var table = await db.DiningTables.FindAsync([order.TableId.Value], ct);
                if (table is not null)
                {
                    table.Status = TableStatus.Available;
                }
            }

            order.RowVersion++;

            db.OrderEvents.Add(new OrderEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                OrderId = orderId,
                EventType = OrderEventType.Completed,
                Details = JsonSerializer.Serialize(new
                {
                    subtotal = bill.Subtotal,
                    serviceAmount = bill.ServiceAmount,
                    taxAmount = bill.TaxAmount,
                    discountAmount = bill.DiscountAmount,
                    total = bill.Total,
                }),
                PerformedBy = tenantContext.UserId,
                PerformedAt = clock.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                orderId = order.Id,
                status = "Completed",
                subtotal = bill.Subtotal,
                serviceAmount = bill.ServiceAmount,
                taxAmount = bill.TaxAmount,
                discountAmount = bill.DiscountAmount,
                total = bill.Total,
                rowVersion = order.RowVersion,
            });
        }).RequirePermission(Permission.CloseOrder);

        // --- Receipt (read-only, render snapshotted order) ---
        group.MapGet("/orders/{orderId:guid}/receipt", async (Guid orderId, PosDbContext db, IClock clock, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "Order is not closed yet");

            var lines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .OrderBy(ol => ol.Sequence)
                .ToListAsync(ct);

            var lineIds = lines.Select(l => l.Id).ToHashSet();
            var modifiers = lineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => lineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var closeEvent = await db.OrderEvents
                .Where(oe => oe.OrderId == orderId && oe.EventType == OrderEventType.Completed)
                .OrderByDescending(oe => oe.PerformedAt)
                .FirstOrDefaultAsync(ct);

            var closedAt = closeEvent?.PerformedAt ?? clock.UtcNow;

            var linesDto = lines
                .Where(l => l.Status != OrderLineStatus.Voided)
                .Select(l => new
                {
                    id = l.Id,
                    itemId = l.ItemId,
                    quantity = l.Quantity,
                    unitPrice = l.UnitPrice,
                    lineTotal = l.UnitPrice * l.Quantity,
                    status = l.Status.ToString(),
                    // Carries the combo a line was expanded from, so a reader can group the
                    // lines and see why the price is an allocated share rather than list price.
                    comboInstanceId = l.ComboInstanceId,
                    comboName = l.ComboName,
                    modifiers = modifiers
                        .Where(m => m.OrderLineId == l.Id)
                        .Select(m => new { name = m.Name, priceDelta = m.PriceDelta }),
                });

            return Results.Ok(new
            {
                branchId = order.BranchId,
                orderId = order.Id,
                tableId = order.TableId,
                dailyNo = order.DailyNo,
                waiterId = order.WaiterId,
                orderType = order.OrderType.ToString(),
                closedAt,
                lines = linesDto,
                subtotal = order.SubtotalAmount,
                serviceAmount = order.ServiceAmount,
                taxAmount = order.TaxAmount,
                discountAmount = order.DiscountAmount,
                total = order.TotalAmount,
            });
        }).RequireAnyPermission(Permission.ReprintReceipt, Permission.CloseOrder);

        // --- Bill preview (non-mutating cashier read) ---
        group.MapGet("/orders/{orderId:guid}/bill-preview", async (Guid orderId, PosDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();

            if (order.Status == OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "Order already closed");

            var lines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .OrderBy(ol => ol.Sequence)
                .ToListAsync(ct);

            var nonVoidedLineIds = lines
                .Where(l => l.Status != OrderLineStatus.Voided)
                .Select(l => l.Id)
                .ToHashSet();

            var modifiers = nonVoidedLineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => nonVoidedLineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var branchSettings = await db.BranchSettings
                .FirstOrDefaultAsync(bs => bs.BranchId == order.BranchId, ct);

            var bill = ComputeBill(lines, modifiers, branchSettings);

            var unfinishedCount = lines.Count(l => l.Status is not (OrderLineStatus.Served or OrderLineStatus.Voided));
            var canClose = unfinishedCount == 0;

            var linesDto = lines
                .Where(l => l.Status != OrderLineStatus.Voided)
                .OrderBy(l => l.Sequence)
                .Select(l => new
                {
                    id = l.Id,
                    itemId = l.ItemId,
                    quantity = l.Quantity,
                    unitPrice = l.UnitPrice,
                    lineTotal = l.UnitPrice * l.Quantity,
                    comboInstanceId = l.ComboInstanceId,
                    comboName = l.ComboName,
                    modifiers = modifiers
                        .Where(m => m.OrderLineId == l.Id)
                        .Select(m => new { name = m.Name, priceDelta = m.PriceDelta }),
                });

            return Results.Ok(new
            {
                orderId = order.Id,
                canClose,
                unfinishedCount,
                lines = linesDto,
                subtotal = bill.Subtotal,
                serviceAmount = bill.ServiceAmount,
                taxAmount = bill.TaxAmount,
                discountAmount = bill.DiscountAmount,
                total = bill.Total,
                rowVersion = order.RowVersion,
            });
        }).RequirePermission(Permission.CloseOrder);

        // --- Print receipt to ESC/POS network printer ---
        group.MapPost("/orders/{orderId:guid}/receipt/print", async (Guid orderId, PosDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FindAsync([orderId], ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Completed)
                return Results.Problem(statusCode: 409, title: "Order is not closed yet");

            var branchSettings = await db.BranchSettings
                .FirstOrDefaultAsync(bs => bs.BranchId == order.BranchId, ct);

            if (branchSettings is null || string.IsNullOrWhiteSpace(branchSettings.PrinterIp) || branchSettings.PrinterPort is null)
                return Results.Problem(statusCode: 400, title: "Printer not configured for this branch");

            var lines = await db.OrderLines
                .Where(ol => ol.OrderId == orderId)
                .OrderBy(ol => ol.Sequence)
                .ToListAsync(ct);

            var lineIds = lines.Select(l => l.Id).ToHashSet();
            var modifiers = lineIds.Count > 0
                ? await db.OrderLineModifiers.Where(olm => lineIds.Contains(olm.OrderLineId)).ToListAsync(ct)
                : [];

            var closeEvent = await db.OrderEvents
                .Where(oe => oe.OrderId == orderId && oe.EventType == OrderEventType.Completed)
                .OrderByDescending(oe => oe.PerformedAt)
                .FirstOrDefaultAsync(ct);

            var bytes = BuildReceiptBytes(order, branchSettings, lines, modifiers, closeEvent?.PerformedAt);

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(IPAddress.Parse(branchSettings.PrinterIp), branchSettings.PrinterPort.Value, ct);
            await using var stream = tcpClient.GetStream();
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);

            return Results.Ok(new { orderId, printed = true });
        }).RequirePermission(Permission.ReprintReceipt);

        return app;
    }

    private static object ComboExpansionResponse(List<OrderLine> lines, int rowVersion) => new
    {
        comboInstanceId = lines[0].ComboInstanceId,
        comboName = lines[0].ComboName,
        lines = lines.Select(l => new { lineId = l.Id, itemId = l.ItemId, sequence = l.Sequence, unitPrice = l.UnitPrice }),
        rowVersion,
    };

    private static byte[] BuildReceiptBytes(Order order, BranchSettings branchSettings, List<OrderLine> lines, List<OrderLineModifier> modifiers, DateTime? closedAt)
    {
        var encoding = Encoding.UTF8;
        var parts = new List<byte>();

        void Ws(string s) => parts.AddRange(encoding.GetBytes(s + "\n"));
        void Wb(byte[] b) => parts.AddRange(b);

        Wb([0x1B, 0x40]); // Initialize printer (ESC @)
        Wb([0x1B, 0x61, 0x01]); // Center align (ESC a 1)
        Wb([0x1B, 0x45, 0x01]); // Bold on (ESC E 1)
        Ws($"Branch: {branchSettings.BranchId}");
        Wb([0x1B, 0x61, 0x00]); // Left align (ESC a 0)
        Wb([0x1B, 0x45, 0x00]); // Bold off (ESC E 0)
        Ws($"Order: {order.Id}");
        Ws(order.TableId is not null ? $"Table: {order.TableId}" : $"Takeaway: {order.DailyNo}");
        Ws($"Waiter: {order.WaiterId}");
        Ws($"Date: {closedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
        Ws(new string('-', 32));

        Guid? printedComboInstance = null;
        foreach (var line in lines.Where(l => l.Status != OrderLineStatus.Voided))
        {
            // Head each run of combo lines with the combo's name. Without it the components
            // carry an allocated share of the bundle price and the customer reads a burger at
            // 3.34 with nothing to explain it.
            if (line.ComboInstanceId != printedComboInstance)
            {
                if (line.ComboInstanceId is not null) Ws($"{line.ComboName}");
                printedComboInstance = line.ComboInstanceId;
            }

            var lineTotal = line.UnitPrice * line.Quantity;
            var indent = line.ComboInstanceId is null ? string.Empty : "  ";
            Ws($"{indent}{line.Quantity}x {line.ItemId} ... {lineTotal:F2}");
            foreach (var mod in modifiers.Where(m => m.OrderLineId == line.Id))
            {
                Ws($"{indent}  + {mod.Name} {mod.PriceDelta:F2}");
            }
        }

        Ws(new string('-', 32));
        Ws($"Subtotal: {order.SubtotalAmount:F2}");
        Ws($"Service: {order.ServiceAmount:F2}");
        Ws($"Tax: {order.TaxAmount:F2}");
        Ws($"Discount: {order.DiscountAmount:F2}");
        Ws($"TOTAL: {order.TotalAmount:F2}");
        Ws("");
        Ws("");
        Wb([0x1D, 0x56, 0x00]); // Full cut (GS V 0)

        return parts.ToArray();
    }

    private static BillBreakdown ComputeBill(
        IReadOnlyList<OrderLine> lines,
        IReadOnlyList<OrderLineModifier> modifiers,
        BranchSettings? branchSettings)
    {
        var subtotal = decimal.Round(lines
            .Where(l => l.Status != OrderLineStatus.Voided)
            .Sum(l =>
            {
                var lineTotal = l.UnitPrice * l.Quantity;
                var modifierSum = modifiers.Where(m => m.OrderLineId == l.Id).Sum(m => m.PriceDelta);
                return lineTotal + modifierSum;
            }), 2, MidpointRounding.AwayFromZero);

        var serviceAmount = (branchSettings?.ServiceApplies ?? false)
            ? decimal.Round(subtotal * (branchSettings!.ServiceChargeRate), 2, MidpointRounding.AwayFromZero)
            : 0m;

        var taxableBase = (branchSettings?.TaxOnService ?? false)
            ? subtotal + serviceAmount
            : subtotal;

        var taxAmount = (branchSettings?.TaxApplies ?? false)
            ? decimal.Round(taxableBase * (branchSettings!.TaxRate), 2, MidpointRounding.AwayFromZero)
            : 0m;

        var discountAmount = 0m;

        var total = subtotal + serviceAmount + taxAmount - discountAmount;

        return new BillBreakdown(subtotal, serviceAmount, taxAmount, discountAmount, total);
    }
}

internal sealed record BillBreakdown(decimal Subtotal, decimal ServiceAmount, decimal TaxAmount, decimal DiscountAmount, decimal Total);

internal sealed record FireBatchRequest(int RowVersion);

internal sealed record CreateDineInOrderRequest(Guid? OrderId);
internal sealed record CreateTakeawayOrderRequest(string? CustomerName, Guid? OrderId = null);
internal sealed record AddDraftLineRequest(Guid MenuItemId, int Quantity, List<Guid> ModifierIds, int RowVersion, Guid? LineId = null);
/// <param name="ComboInstanceId">
/// Client-supplied idempotency key. A combo becomes several lines, so there is no single line id
/// to replay on; this identifies the expansion instead. One combo per call — order two by calling
/// twice with two instance ids.
/// </param>
internal sealed record AddComboRequest(Guid ComboId, int RowVersion, Guid? ComboInstanceId = null);
internal sealed record UpdateDraftLineRequest(int Quantity, List<Guid> ModifierIds, int RowVersion);

internal sealed record VoidLineRequest(string Reason, int RowVersion);

internal sealed record IncreaseQuantityRequest(int QuantityDelta, int RowVersion);

internal sealed record CloseOrderRequest(int RowVersion);
