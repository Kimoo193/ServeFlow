using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;
using Pos.Domain.Entities;
using Pos.Infrastructure.Auth;

namespace Pos.Application.Tests.Auth;

[Collection(RlsCollection.Name)]
public sealed class LoginTests(PostgresRlsFixture fixture)
{
    private const string TestPassword = "testpass123";
    private const string TestSigningKey = "integration-test-signing-key-not-for-production-use-32b";

    [Fact]
    public async Task Happy_path_correct_credentials_returns_token()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "testuser";

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
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResponse = await client.GetAsync("/me");
        meResponse.EnsureSuccessStatusCode();

        var meBody = await meResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tenantId, meBody.GetProperty("tenantId").GetGuid());
        Assert.Equal(branchId, meBody.GetProperty("branchId").GetGuid());
        Assert.Equal(userId, meBody.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Wrong_password_returns_401()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "testuser";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: true, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username,
            password = "wrongpassword",
        });
        Assert.Equal(401, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unknown_username_returns_401_with_identical_body()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "testuser";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: true, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var wrongPasswordResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username,
            password = "wrongpassword",
        });
        var wrongPasswordBody = await wrongPasswordResponse.Content.ReadFromJsonAsync<JsonElement>();

        var unknownUserResponse = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username = "nonexistent",
            password = TestPassword,
        });
        var unknownUserBody = await unknownUserResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(401, (int)wrongPasswordResponse.StatusCode);
        Assert.Equal(401, (int)unknownUserResponse.StatusCode);
        Assert.Equal(
            wrongPasswordBody.GetProperty("title").GetString(),
            unknownUserBody.GetProperty("title").GetString());
        Assert.Equal(
            wrongPasswordBody.GetProperty("detail").GetString(),
            unknownUserBody.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Inactive_user_returns_401()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "inactive";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: false, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username,
            password = TestPassword,
        });
        Assert.Equal(401, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task User_without_password_hash_returns_401()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "nohash";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: true, withPasswordHash: false);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId,
            username,
            password = TestPassword,
        });
        Assert.Equal(401, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Tenant_isolation_prevents_cross_tenant_login()
    {
        var tenantA = Guid.NewGuid();
        var branchA = Guid.NewGuid();
        var userA = Guid.NewGuid();
        const string usernameA = "tenantauser";

        var tenantB = Guid.NewGuid();
        var branchB = Guid.NewGuid();

        await SeedTenantWithUserAsync(tenantA, branchA, userA, usernameA, isActive: true, withPasswordHash: true);
        await SeedTenantBranchOnlyAsync(tenantB, branchB);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId = tenantB,
            branchId = branchB,
            username = usernameA,
            password = TestPassword,
        });
        Assert.Equal(401, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
    }

    [Fact]
    public void PasswordHasher_RoundTrip_returns_true()
    {
        var hash = PasswordHasher.Hash(TestPassword);
        Assert.True(PasswordHasher.Verify(TestPassword, hash));
    }

    [Fact]
    public void PasswordHasher_Verify_wrong_password_returns_false()
    {
        var hash = PasswordHasher.Hash(TestPassword);
        Assert.False(PasswordHasher.Verify("wrongpassword", hash));
    }

    [Fact]
    public void PasswordHasher_Verify_garbage_hash_returns_false()
    {
        Assert.False(PasswordHasher.Verify(TestPassword, "not-a-valid-hash"));
        Assert.False(PasswordHasher.Verify(TestPassword, "pbkdf2$sha256$notanumber$salt$hash"));
        Assert.False(PasswordHasher.Verify(TestPassword, "pbkdf2$sha256$1000$!!!notbase64!!!$!!!notbase64!!!"));
        Assert.False(PasswordHasher.Verify(TestPassword, ""));
    }

    [Fact]
    public async Task Invalid_branch_returns_400()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "branchuser";

        await SeedTenantWithUserAsync(tenantId, branchId, userId, username, isActive: true, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId = Guid.NewGuid(),
            username,
            password = TestPassword,
        });
        Assert.Equal(400, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid branch", body.GetProperty("title").GetString());
    }

    /// <summary>
    /// A caller who has NOT authenticated must not be able to tell a real branch id from a
    /// bogus one. If bad credentials + a valid branch answered differently from bad credentials
    /// + an invalid branch, /auth/login would be an unauthenticated oracle for branch ids.
    /// </summary>
    [Fact]
    public async Task Bad_credentials_do_not_reveal_whether_the_branch_exists()
    {
        var tenantId = Guid.NewGuid();
        var realBranchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string username = "oracleuser";

        await SeedTenantWithUserAsync(tenantId, realBranchId, userId, username, isActive: true, withPasswordHash: true);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var withRealBranch = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId = realBranchId,
            username,
            password = "definitely-the-wrong-password",
        });

        var withBogusBranch = await client.PostAsJsonAsync("/auth/login", new
        {
            tenantId,
            branchId = Guid.NewGuid(),
            username,
            password = "definitely-the-wrong-password",
        });

        var realBody = await withRealBranch.Content.ReadFromJsonAsync<JsonElement>();
        var bogusBody = await withBogusBranch.Content.ReadFromJsonAsync<JsonElement>();

        // traceId is per-request by design, so compare the parts that carry meaning.
        Assert.Equal(401, (int)withRealBranch.StatusCode);
        Assert.Equal(401, (int)withBogusBranch.StatusCode);
        Assert.Equal(
            realBody.GetProperty("title").GetString(),
            bogusBody.GetProperty("title").GetString());
        Assert.Equal(
            realBody.GetProperty("detail").GetString(),
            bogusBody.GetProperty("detail").GetString());
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

    private async Task SeedTenantBranchOnlyAsync(Guid tenantId, Guid branchId)
    {
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

        await transaction.CommitAsync();
    }
}
