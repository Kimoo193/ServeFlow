using Microsoft.EntityFrameworkCore;
using Pos.Domain.Entities;
using Pos.Infrastructure.Auth;

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

    // Development-only credentials — never use in production.
    private const string DevPassword = "devpass123";
    private static readonly string DevPasswordHash = PasswordHasher.Hash(DevPassword);

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

        await UpsertDevUser(dbContext, OwnerUserId, "Dev Owner", Role.Owner, "owner", cancellationToken);
        await UpsertDevUser(dbContext, ManagerUserId, "Dev Manager", Role.Manager, "manager", cancellationToken);
        await UpsertDevUser(dbContext, CashierUserId, "Dev Cashier", Role.Cashier, "cashier", cancellationToken);
        await UpsertDevUser(dbContext, WaiterUserId, "Dev Waiter", Role.Waiter, "waiter", cancellationToken);
        await UpsertDevUser(dbContext, KitchenUserId, "Dev Kitchen", Role.Kitchen, "kitchen", cancellationToken);

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

    private static async Task UpsertDevUser(
        PosDbContext dbContext,
        Guid id,
        string displayName,
        Role role,
        string username,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FindAsync([id], cancellationToken);

        if (user is not null)
        {
            user.Username ??= username;
            user.PasswordHash ??= DevPasswordHash;
            return;
        }

        dbContext.Users.Add(new User
        {
            Id = id,
            TenantId = TenantId,
            DisplayName = displayName,
            Role = role,
            IsActive = true,
            Username = username,
            PasswordHash = DevPasswordHash,
        });
    }
}
