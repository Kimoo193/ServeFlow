using Microsoft.EntityFrameworkCore;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Idempotent Development seed: one Tenant + Branch + BranchSettings + Owner User.
/// Runs under the app's non-superuser role, so it sets app.tenant_id for its own
/// tenant before writing — RLS (FORCE ROW LEVEL SECURITY) applies to this seed too.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid BranchId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    public static async Task SeedAsync(PosDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // See TenantSessionMiddleware for why ExecuteSqlRaw + Guid.ToString("D") is safe here.
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync(
            $"SET LOCAL app.tenant_id = '{TenantId:D}'",
            cancellationToken);
#pragma warning restore EF1002

        if (!await dbContext.Tenants.AnyAsync(t => t.Id == TenantId, cancellationToken))
        {
            dbContext.Tenants.Add(new Tenant
            {
                Id = TenantId,
                Name = "Dev Tenant",
                Type = TenantType.Restaurant,
            });
        }

        if (!await dbContext.Branches.AnyAsync(b => b.Id == BranchId, cancellationToken))
        {
            dbContext.Branches.Add(new Branch
            {
                Id = BranchId,
                TenantId = TenantId,
                Name = "Main Branch",
            });
        }

        if (!await dbContext.BranchSettings.AnyAsync(s => s.BranchId == BranchId, cancellationToken))
        {
            dbContext.BranchSettings.Add(new BranchSettings
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                BranchId = BranchId,
                Currency = "EGP",
                TaxRate = 0.14m,
                TaxApplies = true,
                ServiceChargeRate = 0.12m,
                ServiceApplies = true,
                TaxOnService = false,
            });
        }

        if (!await dbContext.Users.AnyAsync(u => u.Id == OwnerUserId, cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Id = OwnerUserId,
                TenantId = TenantId,
                DisplayName = "Dev Owner",
                Role = Role.Owner,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
