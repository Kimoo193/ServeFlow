using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Pos.Api;
using Pos.Api.Contracts;
using Pos.Api.ExceptionHandling;
using Pos.Application.Common;
using Pos.Domain.Entities;
using Pos.Infrastructure;
using Pos.Infrastructure.Auth;
using Pos.Infrastructure.Persistence;
using System.IdentityModel.Tokens.Jwt;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure();

builder.Services.AddExceptionHandler<InvalidTenantClaimsExceptionHandler>();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.AddExceptionHandler<UniqueViolationExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Configure<IConfiguration> resolves IConfiguration lazily, when JwtBearerOptions is
// first created (after the host is fully built) — not captured early as a closure.
// An early `var jwtSigningKey = builder.Configuration[...]` read here would close over
// whatever value existed at that exact line, which under WebApplicationFactory test
// hosting does not reliably reflect ConfigureAppConfiguration overrides applied later
// in the build pipeline (found by an integration test: /dev/token signed with the
// test's overridden key while this options delegate validated against the original
// appsettings value — a 401 on every request despite a freshly minted, valid token).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, configuration) =>
    {
        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

        // Without this, the handler's legacy inbound claim mapping silently renames
        // well-known short claim names (e.g. "role" -> a long XML claims URI) before
        // ClaimsPrincipal ever sees them. tenant_id/branch_id/user_id aren't "well-known"
        // so they pass through unchanged, but "role" collides — found by manual smoke
        // testing GET /me. Disabling the map makes every claim round-trip verbatim.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        await DevSeeder.SeedAsync(dbContext);
    }

    // Development-only: KimooKit will issue real tokens later. This mints tokens with
    // the same claim shape so local runs and tests can authenticate meanwhile.
    app.MapPost("/dev/token", (DevTokenRequest? request, IConfiguration configuration) =>
    {
        var tenantId = request?.TenantId ?? DevSeeder.TenantId;
        var branchId = request?.BranchId ?? DevSeeder.BranchId;
        var userId = request?.UserId ?? DevSeeder.OwnerUserId;
        var roleValue = request?.Role ?? nameof(Role.Owner);

        // Reject an invalid role at mint time rather than only failing later, differently,
        // whichever endpoint happens to read .Role first.
        if (!Enum.TryParse<Role>(roleValue, ignoreCase: true, out var role) || !Enum.IsDefined(role))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid role",
                detail: $"'{roleValue}' is not a valid role. Valid roles: {string.Join(", ", Enum.GetNames<Role>())}.");
        }

        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtTenantContext.TenantIdClaim, tenantId.ToString()),
            new Claim(JwtTenantContext.BranchIdClaim, branchId.ToString()),
            new Claim(JwtTenantContext.UserIdClaim, userId.ToString()),
            new Claim(JwtTenantContext.RoleClaim, role.ToString()),
        };

        var expires = DateTime.UtcNow.AddHours(8);
        var token = new JwtSecurityToken(claims: claims, expires: expires, signingCredentials: credentials);
        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return Results.Ok(new DevTokenResponse(accessToken, expires));
    });
}

// Registered first so it wraps every subsequent middleware, including
// TenantSessionMiddleware and the endpoint handlers below.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<TenantSessionMiddleware>();

app.MapHealthChecks("/health");

app.MapGet("/me", (ITenantContext tenantContext) => Results.Ok(new
{
    tenantId = tenantContext.TenantId,
    branchId = tenantContext.BranchId,
    userId = tenantContext.UserId,
    role = tenantContext.Role.ToString(),
}))
.RequireAuthorization();

// Deliberately no .Where(b => b.TenantId == ...) — RLS alone must scope this result set,
// proving isolation does not depend on application code getting a filter right.
app.MapGet("/branches", async (PosDbContext dbContext, CancellationToken cancellationToken) =>
    Results.Ok(await dbContext.Branches
        .Select(b => new { b.Id, b.Name })
        .ToListAsync(cancellationToken)))
.RequireAuthorization();

app.MapMenuEndpoints();
app.MapDiningAreaEndpoints();
app.MapOrderEndpoints();

app.Run();

public partial class Program;
