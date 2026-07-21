namespace Pos.Api.Contracts;

public sealed record DevTokenRequest(Guid? TenantId, Guid? BranchId, Guid? UserId, string? Role);
