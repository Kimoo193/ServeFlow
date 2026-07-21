using Microsoft.EntityFrameworkCore;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<BranchSettings> BranchSettings => Set<BranchSettings>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);
    }
}
