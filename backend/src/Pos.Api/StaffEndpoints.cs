using Microsoft.EntityFrameworkCore;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class StaffEndpoints
{
    public static WebApplication MapStaffEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(string.Empty).RequireAuthorization();

        group.MapGet("/users", async (PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Users
                .Select(u => new { u.Id, u.DisplayName, Role = u.Role.ToString(), u.IsActive })
                .ToListAsync(ct))).RequirePermission(Permission.ManageStaff);

        group.MapPost("/users", async (CreateUserRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Role>(body.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
                return Results.Problem(
                    statusCode: 400,
                    title: "Invalid role",
                    detail: $"'{body.Role}' is not a valid role. Valid roles: {string.Join(", ", Enum.GetNames<Role>())}.");

            if (role == Role.Owner && tenantContext.Role != Role.Owner)
                return Results.Problem(
                    statusCode: 403,
                    title: "Insufficient permissions",
                    detail: "Only an Owner may grant the Owner role.");

            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantContext.TenantId,
                DisplayName = body.DisplayName,
                Role = role,
                IsActive = true,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/users/{user.Id}", new { user.Id, user.DisplayName, Role = user.Role.ToString(), user.IsActive });
        }).RequirePermission(Permission.ManageStaff);

        group.MapPut("/users/{id:guid}", async (Guid id, UpdateUserRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();

            if (!Enum.TryParse<Role>(body.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
                return Results.Problem(
                    statusCode: 400,
                    title: "Invalid role",
                    detail: $"'{body.Role}' is not a valid role. Valid roles: {string.Join(", ", Enum.GetNames<Role>())}.");

            if (role == Role.Owner && tenantContext.Role != Role.Owner)
                return Results.Problem(
                    statusCode: 403,
                    title: "Insufficient permissions",
                    detail: "Only an Owner may grant the Owner role.");

            if (id == tenantContext.UserId && (role != user.Role || body.IsActive != user.IsActive))
                return Results.Problem(
                    statusCode: 403,
                    title: "Insufficient permissions",
                    detail: "You cannot change your own role or active status.");

            if (user.Role == Role.Owner && user.IsActive && (role != Role.Owner || !body.IsActive))
            {
                var otherActiveOwners = await db.Users.CountAsync(
                    u => u.Id != user.Id && u.Role == Role.Owner && u.IsActive, ct);
                if (otherActiveOwners == 0)
                    return Results.Problem(
                        statusCode: 409,
                        title: "Last Owner",
                        detail: "The tenant must keep at least one active Owner.");
            }

            user.DisplayName = body.DisplayName;
            user.Role = role;
            user.IsActive = body.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id, user.DisplayName, Role = user.Role.ToString(), user.IsActive });
        }).RequirePermission(Permission.ManageStaff);

        group.MapGet("/roles/permissions", async (PosDbContext db, CancellationToken ct) =>
        {
            var grants = await db.RolePermissions.ToListAsync(ct);
            var roles = Enum.GetNames<Role>();
            var permissions = Enum.GetNames<Permission>();

            var grouped = roles.Select(r => new
            {
                role = r,
                permissions = grants
                    .Where(g => g.Role.ToString() == r)
                    .Select(g => g.Permission.ToString())
                    .ToList(),
            });

            return Results.Ok(new
            {
                roles,
                permissions,
                grants = grouped,
            });
        }).RequirePermission(Permission.ManageStaff);

        group.MapPut("/roles/{role}/permissions", async (string role, SetRolePermissionsRequest body, PosDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Role>(role, ignoreCase: true, out var parsedRole) || !Enum.IsDefined(parsedRole))
                return Results.Problem(
                    statusCode: 400,
                    title: "Invalid role",
                    detail: $"'{role}' is not a valid role. Valid roles: {string.Join(", ", Enum.GetNames<Role>())}.");

            if (parsedRole == Role.Owner)
                return Results.Problem(
                    statusCode: 403,
                    title: "Insufficient permissions",
                    detail: "The Owner role's permissions are fixed.");

            var invalidPermissions = new List<string>();
            var parsedPermissions = new HashSet<Permission>();
            foreach (var p in body.Permissions)
            {
                if (Enum.TryParse<Permission>(p, ignoreCase: true, out var perm) && Enum.IsDefined(perm))
                    parsedPermissions.Add(perm);
                else
                    invalidPermissions.Add(p);
            }

            if (invalidPermissions.Count != 0)
                return Results.Problem(
                    statusCode: 400,
                    title: "Invalid permission(s)",
                    detail: $"Invalid: {string.Join(", ", invalidPermissions)}. Valid permissions: {string.Join(", ", Enum.GetNames<Permission>())}.");

            foreach (var perm in parsedPermissions)
            {
                if (!await PermissionAuthorization.HasPermissionAsync(db, tenantContext, perm, ct))
                    return Results.Problem(
                        statusCode: 403,
                        title: "Insufficient permissions",
                        detail: $"Cannot grant {perm}: you do not hold it.");
            }

            var existing = await db.RolePermissions
                .Where(rp => rp.Role == parsedRole)
                .ToListAsync(ct);
            db.RolePermissions.RemoveRange(existing);

            foreach (var perm in parsedPermissions)
            {
                db.RolePermissions.Add(new RolePermission
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantContext.TenantId,
                    Role = parsedRole,
                    Permission = perm,
                });
            }

            await db.SaveChangesAsync(ct);

            var saved = await db.RolePermissions
                .Where(rp => rp.Role == parsedRole)
                .Select(rp => rp.Permission)
                .ToListAsync(ct);
            var savedStrings = saved.Select(p => p.ToString()).ToList();

            return Results.Ok(new { role, permissions = savedStrings });
        }).RequirePermission(Permission.ManageStaff);

        return app;
    }
}

internal sealed record CreateUserRequest(string DisplayName, string Role);
internal sealed record UpdateUserRequest(string DisplayName, string Role, bool IsActive);
internal sealed record SetRolePermissionsRequest(List<string> Permissions);
