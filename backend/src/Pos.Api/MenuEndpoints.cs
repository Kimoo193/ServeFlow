using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class MenuEndpoints
{
    public static WebApplication MapMenuEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        // --- Categories ---
        group.MapGet("/categories", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Categories
                .OrderBy(c => c.SortOrder)
                .Select(c => new { c.Id, c.Name, c.SortOrder })
                .ToListAsync(ct)));

        group.MapGet("/categories/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var category = await db.Categories.FindAsync([id], ct);
            return category is null ? Results.NotFound() : Results.Ok(new { category.Id, category.Name, category.SortOrder });
        });

        group.MapPost("/categories", async (CreateCategoryRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var category = new Category
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                Name = body.Name,
                SortOrder = body.SortOrder,
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/categories/{category.Id}", new { category.Id, category.Name, category.SortOrder });
        });

        group.MapPut("/categories/{id:guid}", async (Guid id, CreateCategoryRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var category = await db.Categories.FindAsync([id], ct);
            if (category is null) return Results.NotFound();
            category.Name = body.Name;
            category.SortOrder = body.SortOrder;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { category.Id, category.Name, category.SortOrder });
        });

        group.MapDelete("/categories/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var category = await db.Categories.FindAsync([id], ct);
            if (category is null) return Results.NotFound();
            db.Categories.Remove(category);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Menu Items ---
        group.MapGet("/menu-items", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.MenuItems
                .Select(m => new { m.Id, m.CategoryId, m.StationId, m.Name, m.Price, Availability = m.Availability.ToString() })
                .ToListAsync(ct)));

        group.MapGet("/menu-items/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var item = await db.MenuItems.FindAsync([id], ct);
            return item is null ? Results.NotFound() : Results.Ok(new { item.Id, item.CategoryId, item.StationId, item.Name, item.Price, Availability = item.Availability.ToString() });
        });

        group.MapPost("/menu-items", async (CreateMenuItemRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Availability>(body.Availability, ignoreCase: true, out var availability) || !Enum.IsDefined(availability))
                return Results.Problem(statusCode: 400, title: "Invalid availability", detail: $"Must be one of: {string.Join(", ", Enum.GetNames<Availability>())}");

            var item = new MenuItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                CategoryId = body.CategoryId,
                StationId = body.StationId,
                Name = body.Name,
                Price = body.Price,
                Availability = availability,
            };
            db.MenuItems.Add(item);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/menu-items/{item.Id}", new { item.Id, item.CategoryId, item.StationId, item.Name, item.Price, Availability = item.Availability.ToString() });
        });

        group.MapPut("/menu-items/{id:guid}", async (Guid id, CreateMenuItemRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var item = await db.MenuItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            if (!Enum.TryParse<Availability>(body.Availability, ignoreCase: true, out var availability) || !Enum.IsDefined(availability))
                return Results.Problem(statusCode: 400, title: "Invalid availability", detail: $"Must be one of: {string.Join(", ", Enum.GetNames<Availability>())}");
            item.CategoryId = body.CategoryId;
            item.StationId = body.StationId;
            item.Name = body.Name;
            item.Price = body.Price;
            item.Availability = availability;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { item.Id, item.CategoryId, item.StationId, item.Name, item.Price, Availability = item.Availability.ToString() });
        });

        group.MapDelete("/menu-items/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var item = await db.MenuItems.FindAsync([id], ct);
            if (item is null) return Results.NotFound();
            db.MenuItems.Remove(item);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Item-ModifierGroup attachments ---
        group.MapPost("/menu-items/{itemId:guid}/modifier-groups/{groupId:guid}", async (Guid itemId, Guid groupId, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var exists = await db.ItemModifierGroups.AnyAsync(img => img.ItemId == itemId && img.GroupId == groupId, ct);
            if (exists) return Results.Conflict();
            var link = new ItemModifierGroup { Id = Guid.NewGuid(), TenantId = tenantContext.TenantId, ItemId = itemId, GroupId = groupId };
            db.ItemModifierGroups.Add(link);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/menu-items/{itemId}/modifier-groups/{groupId}", new { link.Id });
        });

        group.MapDelete("/menu-items/{itemId:guid}/modifier-groups/{groupId:guid}", async (Guid itemId, Guid groupId, PosDbContext db, CancellationToken ct) =>
        {
            var link = await db.ItemModifierGroups.FirstOrDefaultAsync(img => img.ItemId == itemId && img.GroupId == groupId, ct);
            if (link is null) return Results.NotFound();
            db.ItemModifierGroups.Remove(link);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Modifier Groups ---
        group.MapGet("/modifier-groups", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ModifierGroups
                .Select(mg => new { mg.Id, mg.Name, mg.MinSelect, mg.MaxSelect, mg.Required })
                .ToListAsync(ct)));

        group.MapGet("/modifier-groups/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var mg = await db.ModifierGroups.FindAsync([id], ct);
            return mg is null ? Results.NotFound() : Results.Ok(new { mg.Id, mg.Name, mg.MinSelect, mg.MaxSelect, mg.Required });
        });

        group.MapPost("/modifier-groups", async (CreateModifierGroupRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var mg = new ModifierGroup
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                Name = body.Name,
                MinSelect = body.MinSelect,
                MaxSelect = body.MaxSelect,
                Required = body.Required,
            };
            db.ModifierGroups.Add(mg);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/modifier-groups/{mg.Id}", new { mg.Id, mg.Name, mg.MinSelect, mg.MaxSelect, mg.Required });
        });

        group.MapPut("/modifier-groups/{id:guid}", async (Guid id, CreateModifierGroupRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var mg = await db.ModifierGroups.FindAsync([id], ct);
            if (mg is null) return Results.NotFound();
            mg.Name = body.Name;
            mg.MinSelect = body.MinSelect;
            mg.MaxSelect = body.MaxSelect;
            mg.Required = body.Required;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { mg.Id, mg.Name, mg.MinSelect, mg.MaxSelect, mg.Required });
        });

        group.MapDelete("/modifier-groups/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var mg = await db.ModifierGroups.FindAsync([id], ct);
            if (mg is null) return Results.NotFound();
            db.ModifierGroups.Remove(mg);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Modifiers ---
        group.MapGet("/modifiers", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Modifiers
                .Select(m => new { m.Id, m.GroupId, m.Name, m.PriceDelta })
                .ToListAsync(ct)));

        group.MapGet("/modifiers/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var modifier = await db.Modifiers.FindAsync([id], ct);
            return modifier is null ? Results.NotFound() : Results.Ok(new { modifier.Id, modifier.GroupId, modifier.Name, modifier.PriceDelta });
        });

        group.MapPost("/modifiers", async (CreateModifierRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var modifier = new Modifier
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                GroupId = body.GroupId,
                Name = body.Name,
                PriceDelta = body.PriceDelta,
            };
            db.Modifiers.Add(modifier);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/modifiers/{modifier.Id}", new { modifier.Id, modifier.GroupId, modifier.Name, modifier.PriceDelta });
        });

        group.MapPut("/modifiers/{id:guid}", async (Guid id, CreateModifierRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var modifier = await db.Modifiers.FindAsync([id], ct);
            if (modifier is null) return Results.NotFound();
            modifier.GroupId = body.GroupId;
            modifier.Name = body.Name;
            modifier.PriceDelta = body.PriceDelta;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { modifier.Id, modifier.GroupId, modifier.Name, modifier.PriceDelta });
        });

        group.MapDelete("/modifiers/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var modifier = await db.Modifiers.FindAsync([id], ct);
            if (modifier is null) return Results.NotFound();
            db.Modifiers.Remove(modifier);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Combos ---
        group.MapGet("/combos", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Combos
                .Select(c => new { c.Id, c.Name, c.Price })
                .ToListAsync(ct)));

        group.MapGet("/combos/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var combo = await db.Combos.FindAsync([id], ct);
            return combo is null ? Results.NotFound() : Results.Ok(new { combo.Id, combo.Name, combo.Price });
        });

        group.MapPost("/combos", async (CreateComboRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var combo = new Combo { Id = Guid.NewGuid(), TenantId = tenantContext.TenantId, Name = body.Name, Price = body.Price };
            db.Combos.Add(combo);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/combos/{combo.Id}", new { combo.Id, combo.Name, combo.Price });
        });

        group.MapPut("/combos/{id:guid}", async (Guid id, CreateComboRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var combo = await db.Combos.FindAsync([id], ct);
            if (combo is null) return Results.NotFound();
            combo.Name = body.Name;
            combo.Price = body.Price;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { combo.Id, combo.Name, combo.Price });
        });

        group.MapDelete("/combos/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var combo = await db.Combos.FindAsync([id], ct);
            if (combo is null) return Results.NotFound();
            db.Combos.Remove(combo);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Combo-Item attachments ---
        group.MapPost("/combos/{comboId:guid}/items/{itemId:guid}", async (Guid comboId, Guid itemId, AttachComboItemRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var exists = await db.ComboItems.AnyAsync(ci => ci.ComboId == comboId && ci.ItemId == itemId, ct);
            if (exists) return Results.Conflict();
            var ci = new ComboItem { Id = Guid.NewGuid(), TenantId = tenantContext.TenantId, ComboId = comboId, ItemId = itemId, Quantity = body.Quantity };
            db.ComboItems.Add(ci);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/combos/{comboId}/items/{itemId}", new { ci.Id, ci.Quantity });
        });

        group.MapDelete("/combos/{comboId:guid}/items/{itemId:guid}", async (Guid comboId, Guid itemId, PosDbContext db, CancellationToken ct) =>
        {
            var ci = await db.ComboItems.FirstOrDefaultAsync(x => x.ComboId == comboId && x.ItemId == itemId, ct);
            if (ci is null) return Results.NotFound();
            db.ComboItems.Remove(ci);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Stations ---
        group.MapGet("/stations", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Stations
                .Select(s => new { s.Id, s.BranchId, s.Name })
                .ToListAsync(ct)));

        group.MapGet("/stations/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var station = await db.Stations.FindAsync([id], ct);
            return station is null ? Results.NotFound() : Results.Ok(new { station.Id, station.BranchId, station.Name });
        });

        group.MapPost("/stations", async (CreateStationRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var station = new Station { Id = Guid.NewGuid(), TenantId = tenantContext.TenantId, BranchId = body.BranchId, Name = body.Name };
            db.Stations.Add(station);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/stations/{station.Id}", new { station.Id, station.BranchId, station.Name });
        });

        group.MapPut("/stations/{id:guid}", async (Guid id, CreateStationRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var station = await db.Stations.FindAsync([id], ct);
            if (station is null) return Results.NotFound();
            station.BranchId = body.BranchId;
            station.Name = body.Name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { station.Id, station.BranchId, station.Name });
        });

        group.MapDelete("/stations/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var station = await db.Stations.FindAsync([id], ct);
            if (station is null) return Results.NotFound();
            db.Stations.Remove(station);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // --- Menu tree (tablet: Hidden excluded) ---
        group.MapGet("/menu", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await BuildMenuTreeAsync(db, includeHidden: false, ct)));

        // --- Admin menu tree: includes Hidden items ---
        group.MapGet("/admin/menu", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await BuildMenuTreeAsync(db, includeHidden: true, ct)));

        return app;
    }

    private static async Task<object> BuildMenuTreeAsync(PosDbContext db, bool includeHidden, CancellationToken ct)
    {
        var categories = await db.Categories.OrderBy(c => c.SortOrder).ToListAsync(ct);

        var items = includeHidden
            ? await db.MenuItems.ToListAsync(ct)
            : await db.MenuItems.Where(mi => mi.Availability != Availability.Hidden).ToListAsync(ct);

        var itemIds = items.Select(i => i.Id).ToHashSet();
        var links = await db.ItemModifierGroups.Where(img => itemIds.Contains(img.ItemId)).ToListAsync(ct);
        var groupIds = links.Select(l => l.GroupId).ToHashSet();
        var groups = await db.ModifierGroups.Where(mg => groupIds.Contains(mg.Id)).ToListAsync(ct);
        var groupIdSet = groups.Select(g => g.Id).ToHashSet();
        var modifiers = await db.Modifiers.Where(m => groupIdSet.Contains(m.GroupId)).ToListAsync(ct);
        var combos = await db.Combos.ToListAsync(ct);
        var comboItemIds = combos.Select(c => c.Id).ToHashSet();
        var comboItems = comboItemIds.Count > 0 ? await db.ComboItems.Where(ci => comboItemIds.Contains(ci.ComboId)).ToListAsync(ct) : [];

        return new
        {
            categories = categories.Select(c => new
            {
                c.Id,
                c.Name,
                c.SortOrder,
                items = items.Where(i => i.CategoryId == c.Id).Select(i => new
                {
                    i.Id,
                    i.Name,
                    i.Price,
                    Availability = i.Availability.ToString(),
                    i.StationId,
                    modifierGroups = links.Where(l => l.ItemId == i.Id)
                        .Select(l => groups.First(g => g.Id == l.GroupId))
                        .Select(g => new
                        {
                            g.Id,
                            g.Name,
                            g.MinSelect,
                            g.MaxSelect,
                            g.Required,
                            modifiers = modifiers.Where(m => m.GroupId == g.Id).Select(m => new
                            {
                                m.Id,
                                m.Name,
                                m.PriceDelta,
                            }),
                        }),
                }),
            }),
            combos = combos.Select(c => new
            {
                c.Id,
                c.Name,
                c.Price,
                items = comboItems.Where(ci => ci.ComboId == c.Id).Select(ci => new
                {
                    itemId = ci.ItemId,
                    ci.Quantity,
                }),
            }),
        };
    }
}

// --- Request DTOs ---
internal sealed record CreateCategoryRequest(string Name, int SortOrder);
internal sealed record CreateMenuItemRequest(Guid CategoryId, Guid StationId, string Name, decimal Price, string Availability);
internal sealed record CreateModifierGroupRequest(string Name, int MinSelect, int MaxSelect, bool Required);
internal sealed record CreateModifierRequest(Guid GroupId, string Name, decimal PriceDelta);
internal sealed record CreateComboRequest(string Name, decimal Price);
internal sealed record CreateStationRequest(Guid BranchId, string Name);
internal sealed record AttachComboItemRequest(int Quantity);
