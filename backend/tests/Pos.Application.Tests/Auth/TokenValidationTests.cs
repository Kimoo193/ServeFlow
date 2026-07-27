using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Pos.Application.Tests.Rls;
using Pos.Domain.Entities;
using Pos.Infrastructure.Auth;

namespace Pos.Application.Tests.Auth;

[Collection(RlsCollection.Name)]
public sealed class TokenValidationTests(PostgresRlsFixture fixture)
{
    private const string TestPassword = "testpass123";
    private const string TestSigningKey = "integration-test-signing-key-not-for-production-use-32b";
    private const string CorrectIssuer = "serveflow-api";
    private const string CorrectAudience = "serveflow-clients";

    /// <summary>
    /// Proves that a token minted by /auth/login — with the correct iss/aud from config —
    /// is accepted on an authenticated endpoint. If this fails, the round-trip is broken.
    /// </summary>
    [Fact]
    public async Task Token_from_login_is_accepted_on_authenticated_endpoint()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "validationuser";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: true, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username,
            password = TestPassword,
        });
        Assert.Equal(200, (int)loginResponse.StatusCode);

        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();
        Assert.NotNull(accessToken);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResponse = await client.GetAsync("/me");
        meResponse.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A token signed with the correct key but a different issuer must be rejected.
    /// This is the exact scenario ValidateIssuer protects against — e.g. a shared
    /// signing key between staging and production where staging tokens carry a
    /// different issuer.
    /// </summary>
    [Fact]
    public async Task Token_with_wrong_issuer_is_rejected()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = MintToken(issuer: "wrong-issuer", audience: CorrectAudience);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/me");
        Assert.Equal(401, (int)response.StatusCode);
    }

    /// <summary>
    /// A token signed with the correct key but a different audience must be rejected.
    /// This is the companion to the issuer test — ValidateAudience being false would
    /// accept tokens minted for a different intended consumer.
    /// </summary>
    [Fact]
    public async Task Token_with_wrong_audience_is_rejected()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = MintToken(issuer: CorrectIssuer, audience: "wrong-audience");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/me");
        Assert.Equal(401, (int)response.StatusCode);
    }

    private static string MintToken(string issuer, string audience)
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtTenantContext.TenantIdClaim, tenantId.ToString()),
            new Claim(JwtTenantContext.BranchIdClaim, branchId.ToString()),
            new Claim(JwtTenantContext.UserIdClaim, userId.ToString()),
            new Claim(JwtTenantContext.RoleClaim, nameof(Role.Waiter)),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = fixture.AppConnectionString,
                    ["ConnectionStrings:Redis"] = "localhost:6389,abortConnect=false",
                    ["Jwt:SigningKey"] = TestSigningKey,
                });
            });
        });
    }

    private async Task SeedTenantWithUserAsync(
        Guid tenantId,
        Guid branchId,
        Guid userId,
        string username,
        bool isActive,
        bool withPasswordHash,
        Role role = Role.Waiter)
    {
        var passwordHash = withPasswordHash ? PasswordHasher.Hash(TestPassword) : null;
        var isActiveValue = isActive ? "true" : "false";
        var passwordHashValue = passwordHash is not null
            ? $"'{passwordHash}'"
            : "NULL";

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using (var insertTenant = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')", connection))
        {
            insertTenant.Parameters.AddWithValue("id", tenantId);
            insertTenant.Parameters.AddWithValue("name", $"Tenant {tenantId:N}");
            await insertTenant.ExecuteNonQueryAsync();
        }

        await using (var insertBranch = new NpgsqlCommand(
            "INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, @name)", connection))
        {
            insertBranch.Parameters.AddWithValue("id", branchId);
            insertBranch.Parameters.AddWithValue("tenantId", tenantId);
            insertBranch.Parameters.AddWithValue("name", "Test Branch");
            await insertBranch.ExecuteNonQueryAsync();
        }

        await using (var insertUser = new NpgsqlCommand(
            $"INSERT INTO users (id, tenant_id, display_name, role, username, password_hash, is_active) VALUES (@id, @tenantId, @displayName, @role, @username, {passwordHashValue}, {isActiveValue})", connection))
        {
            insertUser.Parameters.AddWithValue("id", userId);
            insertUser.Parameters.AddWithValue("tenantId", tenantId);
            insertUser.Parameters.AddWithValue("displayName", username);
            insertUser.Parameters.AddWithValue("role", role.ToString());
            insertUser.Parameters.AddWithValue("username", username);
            await insertUser.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
