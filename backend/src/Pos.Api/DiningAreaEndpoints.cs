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
        });

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
        });

        return app;
    }
}

internal sealed record CreateSectionRequest(string Name);
internal sealed record CreateTableRequest(Guid SectionId, string Label);
