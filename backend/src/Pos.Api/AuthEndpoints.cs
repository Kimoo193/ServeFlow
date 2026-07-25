using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pos.Infrastructure.Auth;
using Pos.Infrastructure.Persistence;

namespace Pos.Api;

internal static class AuthEndpoints
{
    private static readonly string DummyHash = PasswordHasher.Hash("dummy-for-timing-purposes-only");

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (
            LoginRequest request,
            PosDbContext dbContext,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username)
                || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation failed",
                    detail: "Username and password are required.");
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            await dbContext.Database.SetTenantAsync(request.TenantId, ct);

            var user = await dbContext.Users
                .Where(u => u.Username == request.Username)
                .FirstOrDefaultAsync(ct);

            var hashToVerify = user?.PasswordHash ?? DummyHash;
            var passwordValid = PasswordHasher.Verify(request.Password, hashToVerify);

            var branchExists = await dbContext.Branches.AnyAsync(b => b.Id == request.BranchId, ct);

            await transaction.CommitAsync(ct);

            // Credentials are judged BEFORE the branch is. Both queries have already run, so
            // the timing is unchanged — but answering "bad branch" (400) to a caller who never
            // proved who they are turns this endpoint into an oracle for which branch ids exist
            // in a tenant. Only a caller who has authenticated learns anything about branches.
            var canLogin = user is { IsActive: true, PasswordHash: not null } && passwordValid;

            if (!canLogin)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Authentication failed",
                    detail: "Invalid username or password.");
            }

            if (!branchExists)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid branch",
                    detail: "The specified branch does not belong to this tenant.");
            }

            var claimsUser = user!;

            var signingKey = configuration["Jwt:SigningKey"]
                ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtTenantContext.TenantIdClaim, claimsUser.TenantId.ToString()),
                new Claim(JwtTenantContext.BranchIdClaim, request.BranchId.ToString()),
                new Claim(JwtTenantContext.UserIdClaim, claimsUser.Id.ToString()),
                new Claim(JwtTenantContext.RoleClaim, claimsUser.Role.ToString()),
            };

            var expires = DateTime.UtcNow.AddHours(8);
            var token = new JwtSecurityToken(claims: claims, expires: expires, signingCredentials: credentials);
            var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

            return Results.Ok(new LoginResponse(
                accessToken,
                new DateTimeOffset(expires, TimeSpan.Zero),
                claimsUser.Role.ToString(),
                claimsUser.Id));
        })
        .RequireRateLimiting("login");

        return app;
    }
}

internal sealed record LoginRequest(Guid TenantId, Guid BranchId, string Username, string Password);

internal sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string Role, Guid UserId);
