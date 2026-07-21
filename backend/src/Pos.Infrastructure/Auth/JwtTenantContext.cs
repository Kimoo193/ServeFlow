using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pos.Application.Common;
using Pos.Domain.Entities;

namespace Pos.Infrastructure.Auth;

/// <summary>
/// Reads tenant/branch/user/role from the authenticated request's claims.
/// KimooKit will issue the JWTs carrying these claims; until then, the
/// Development-only /dev/token endpoint mints tokens with the same shape.
/// </summary>
public sealed class JwtTenantContext : ITenantContext
{
    public const string TenantIdClaim = "tenant_id";
    public const string BranchIdClaim = "branch_id";
    public const string UserIdClaim = "user_id";
    public const string RoleClaim = "role";

    private readonly Lazy<Guid> _tenantId;
    private readonly Lazy<Guid> _branchId;
    private readonly Lazy<Guid> _userId;
    private readonly Lazy<Role> _role;

    public JwtTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        var user = httpContextAccessor.HttpContext?.User
            ?? throw new InvalidOperationException("No HTTP context is available to resolve tenant context.");

        _tenantId = new Lazy<Guid>(() => ReadGuidClaim(user, TenantIdClaim));
        _branchId = new Lazy<Guid>(() => ReadGuidClaim(user, BranchIdClaim));
        _userId = new Lazy<Guid>(() => ReadGuidClaim(user, UserIdClaim));
        _role = new Lazy<Role>(() => ReadRoleClaim(user));
    }

    public Guid TenantId => _tenantId.Value;

    public Guid BranchId => _branchId.Value;

    public Guid UserId => _userId.Value;

    public Role Role => _role.Value;

    private static Guid ReadGuidClaim(ClaimsPrincipal user, string claimType)
    {
        var value = user.FindFirst(claimType)?.Value
            ?? throw new InvalidOperationException($"Required claim '{claimType}' is missing from the authenticated principal.");

        return Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Claim '{claimType}' value '{value}' is not a valid GUID.");
    }

    private static Role ReadRoleClaim(ClaimsPrincipal user)
    {
        var value = user.FindFirst(RoleClaim)?.Value
            ?? throw new InvalidOperationException($"Required claim '{RoleClaim}' is missing from the authenticated principal.");

        return Enum.TryParse<Role>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Claim '{RoleClaim}' value '{value}' is not a valid role.");
    }
}
