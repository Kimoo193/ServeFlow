namespace Pos.Application.Common;

/// <summary>
/// The authenticated principal's claims do not satisfy <see cref="ITenantContext"/>'s
/// contract (a required claim is missing, or its value doesn't parse). This is a bad-token
/// condition, not a server fault — callers map it to 401, never let it surface as a 500.
/// </summary>
public sealed class InvalidTenantClaimsException(string message) : Exception(message);
