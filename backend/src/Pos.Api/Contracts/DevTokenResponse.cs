namespace Pos.Api.Contracts;

public sealed record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
