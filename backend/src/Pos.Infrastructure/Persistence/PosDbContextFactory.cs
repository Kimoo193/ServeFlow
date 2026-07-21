using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Design-time only: lets `dotnet ef migrations add` build PosDbContext without running
/// the full Pos.Api host (and its Development-only seed step, which needs a live schema
/// to already exist). Never used at runtime — Pos.Api's own DI registration owns that.
/// </summary>
public sealed class PosDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PosDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Port=5442;Database=pos;Username=pos_app;Password=pos_app_dev_password");

        return new PosDbContext(optionsBuilder.Options);
    }
}
