using Pos.Domain.Entities;

namespace Pos.Application.Common;

/// <summary>
/// Resolves the authenticated caller's tenant/branch/user/role from the request.
/// KimooKit is the future token issuer; this port lets Application/Infrastructure
/// code depend only on the resolved values, never on how the JWT is parsed.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }

    Guid BranchId { get; }

    Guid UserId { get; }

    Role Role { get; }
}
