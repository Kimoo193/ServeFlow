using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class DiningAreaEndpoints
{
    public static WebApplication MapDiningAreaEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        group.MapGet("/sections", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sections
                .Select(s => new { s.Id, s.BranchId, s.Name })
                .ToListAsync(ct)));

        group.MapPost("/sections", async (CreateSectionRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var section = new Section
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                BranchId = tenantContext.BranchId,
                Name = body.Name,
            };
            db.Sections.Add(section);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/sections/{section.Id}", new { section.Id, section.BranchId, section.Name });
        }).RequirePermission(Permission.ManageFloor);

        group.MapGet("/tables", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DiningTables
                .Select(t => new { t.Id, t.Label, Status = t.Status.ToString(), t.SectionId })
                .ToListAsync(ct)));

        group.MapPost("/tables", async (CreateTableRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var table = new DiningTable
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                SectionId = body.SectionId,
                Label = body.Label,
            };
            db.DiningTables.Add(table);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/tables/{table.Id}", new { table.Id, table.Label, Status = table.Status.ToString(), table.SectionId });
        }).RequirePermission(Permission.ManageFloor);

        group.MapPut("/sections/{id:guid}", async (Guid id, CreateSectionRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var section = await db.Sections.FindAsync([id], ct);
            if (section is null) return Results.NotFound();
            section.Name = body.Name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { section.Id, section.BranchId, section.Name });
        }).RequirePermission(Permission.ManageFloor);

        group.MapDelete("/sections/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var section = await db.Sections.FindAsync([id], ct);
            if (section is null) return Results.NotFound();

            if (await db.DiningTables.AnyAsync(t => t.SectionId == id, ct))
                return Results.Problem(statusCode: 409, title: "Section has tables", detail: "Remove or reassign its tables before deleting the section");

            db.Sections.Remove(section);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(Permission.ManageFloor);

        group.MapPut("/tables/{id:guid}", async (Guid id, CreateTableRequest body, PosDbContext db, CancellationToken ct) =>
        {
            var table = await db.DiningTables.FindAsync([id], ct);
            if (table is null) return Results.NotFound();
            table.SectionId = body.SectionId;
            table.Label = body.Label;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { table.Id, table.Label, Status = table.Status.ToString(), table.SectionId });
        }).RequirePermission(Permission.ManageFloor);

        group.MapDelete("/tables/{id:guid}", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var table = await db.DiningTables.FindAsync([id], ct);
            if (table is null) return Results.NotFound();

            if (await db.Orders.AnyAsync(o => o.TableId == id && (o.Status == OrderStatus.Open || o.Status == OrderStatus.InProgress), ct))
                return Results.Problem(statusCode: 409, title: "Table has an active order", detail: "Close or cancel the order before deleting the table");

            db.DiningTables.Remove(table);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(Permission.ManageFloor);

        return app;
    }
}

internal sealed record CreateSectionRequest(string Name);
internal sealed record CreateTableRequest(Guid SectionId, string Label);
