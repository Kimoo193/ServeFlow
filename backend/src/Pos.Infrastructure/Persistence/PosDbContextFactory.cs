using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Design-time only: lets `dotnet ef migrations add`/`database update` build PosDbContext
/// without running the full Pos.Api host. Never used at runtime — Pos.Api's own DI
/// registration owns that, and always connects as pos_app, never this admin role.
///
/// Uses the admin/owner connection, not pos_app: pos_app is granted only
/// SELECT/INSERT/UPDATE/DELETE (002_create_app_role.sql), no DDL — migrations must run
/// as the admin role per CLAUDE.md ("the EF migration ... runs as an ADMIN/owner DB role").
/// `dotnet ef database update` falls back to this factory whenever no --connection is
/// passed, so pointing it at pos_app would fail every such invocation with
/// permission-denied on the first CREATE TABLE.
/// </summary>
public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PosDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5442;Database=pos;Username=pos_owner;Password=pos_owner_dev_password");

        return new PosDbContext(optionsBuilder.Options);
    }
}
