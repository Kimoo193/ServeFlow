using Microsoft.EntityFrameworkCore;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Persistence;

/// <summary>
/// Idempotent Development seed: one Tenant + Branch + BranchSettings + one User per role.
/// Runs under the app's non-superuser role, so it sets app.tenant_id for its own
/// tenant before writing — RLS (FORCE ROW LEVEL SECURITY) applies to this seed too.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid BranchId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid ManagerUserId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    public static readonly Guid CashierUserId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public static readonly Guid WaiterUserId = Guid.Parse("00000000-0000-0000-0000-000000000006");
    public static readonly Guid KitchenUserId = Guid.Parse("00000000-0000-0000-0000-000000000007");

    public static async Task SeedAsync(PosDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await dbContext.Database.SetTenantAsync(TenantId, cancellationToken);

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

        if (!await dbContext.Users.AnyAsync(u => u.Id == ManagerUserId, cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Id = ManagerUserId,
                TenantId = TenantId,
                DisplayName = "Dev Manager",
                Role = Role.Manager,
            });
        }

        if (!await dbContext.Users.AnyAsync(u => u.Id == CashierUserId, cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Id = CashierUserId,
                TenantId = TenantId,
                DisplayName = "Dev Cashier",
                Role = Role.Cashier,
            });
        }

        if (!await dbContext.Users.AnyAsync(u => u.Id == WaiterUserId, cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Id = WaiterUserId,
                TenantId = TenantId,
                DisplayName = "Dev Waiter",
                Role = Role.Waiter,
            });
        }

        if (!await dbContext.Users.AnyAsync(u => u.Id == KitchenUserId, cancellationToken))
        {
            dbContext.Users.Add(new User
            {
                Id = KitchenUserId,
                TenantId = TenantId,
                DisplayName = "Dev Kitchen",
                Role = Role.Kitchen,
            });
        }

        if (!await dbContext.RolePermissions.AnyAsync(rp => rp.TenantId == TenantId, cancellationToken))
        {
            var allPermissions = Enum.GetValues<Permission>();
            foreach (var perm in allPermissions)
            {
                dbContext.RolePermissions.Add(new RolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantId,
                    Role = Role.Manager,
                    Permission = perm,
                });
            }

            dbContext.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Role = Role.Cashier,
                Permission = Permission.CloseOrder,
            });
            dbContext.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Role = Role.Cashier,
                Permission = Permission.ViewHistory,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
