using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests.Staff;

[Collection(RlsCollection.Name)]
public sealed class StaffAdminTests(PostgresRlsFixture fixture)
{
    [Fact]
    public async Task Post_user_creates_user_appearing_in_get_with_isActive_true()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);

        var postResponse = await client.PostAsJsonAsync("/users", new { displayName = "Alice", role = "Waiter" });
        Assert.Equal(201, (int)postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alice", created.GetProperty("displayName").GetString());
        Assert.Equal("Waiter", created.GetProperty("role").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        var getResponse = await client.GetAsync("/users");
        getResponse.EnsureSuccessStatusCode();
        var users = await getResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var alice = users!.Single(u => u.GetProperty("displayName").GetString() == "Alice");
        Assert.Equal("Waiter", alice.GetProperty("role").GetString());
        Assert.True(alice.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Put_user_updates_displayName_role_and_can_deactivate()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedUserAsync(tenantId, userId, "Bob", "Kitchen");

        var putResponse = await client.PutAsJsonAsync($"/users/{userId}", new { displayName = "Bob Updated", role = "Cashier", isActive = false });
        Assert.Equal(200, (int)putResponse.StatusCode);

        var getResponse = await client.GetAsync("/users");
        getResponse.EnsureSuccessStatusCode();
        var users = await getResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var bob = users!.Single(u => u.GetProperty("id").GetGuid() == userId);
        Assert.Equal("Bob Updated", bob.GetProperty("displayName").GetString());
        Assert.Equal("Cashier", bob.GetProperty("role").GetString());
        Assert.False(bob.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Post_user_with_invalid_role_returns_400()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);

        var response = await client.PostAsJsonAsync("/users", new { displayName = "Charlie", role = "SuperAdmin" });
        Assert.Equal(400, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid role", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Put_user_with_invalid_role_returns_400()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedUserAsync(tenantId, userId, "Diana", "Waiter");

        var response = await client.PutAsJsonAsync($"/users/{userId}", new { displayName = "Diana", role = "InvalidRole", isActive = true });
        Assert.Equal(400, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid role", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Put_set_role_permissions_then_get_shows_grants()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);

        var putResponse = await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = new[] { "VoidLine", "CloseOrder" } });
        Assert.Equal(200, (int)putResponse.StatusCode);
        var putResult = await putResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Manager", putResult.GetProperty("role").GetString());
        Assert.Equal(2, putResult.GetProperty("permissions").GetArrayLength());

        var getResponse = await client.GetAsync("/roles/permissions");
        getResponse.EnsureSuccessStatusCode();
        var matrix = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

        var roles = matrix.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("Manager", roles);

        var permissions = matrix.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("VoidLine", permissions);
        Assert.Contains("CloseOrder", permissions);
        Assert.Contains("EditMenu", permissions);

        var grants = matrix.GetProperty("grants").EnumerateArray().ToList();
        var mgrGrant = grants.Single(g => g.GetProperty("role").GetString() == "Manager");
        var mgrPerms = mgrGrant.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(2, mgrPerms.Count);
        Assert.Contains("VoidLine", mgrPerms);
        Assert.Contains("CloseOrder", mgrPerms);
    }

    [Fact]
    public async Task Put_role_permissions_with_empty_list_clears_them()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);

        await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = new[] { "VoidLine" } });

        var clearResponse = await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = Array.Empty<string>() });
        Assert.Equal(200, (int)clearResponse.StatusCode);
        var clearResult = await clearResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(clearResult.GetProperty("permissions").EnumerateArray());

        var getResponse = await client.GetAsync("/roles/permissions");
        getResponse.EnsureSuccessStatusCode();
        var matrix = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var grants = matrix.GetProperty("grants").EnumerateArray().ToList();
        var mgrGrant = grants.Single(g => g.GetProperty("role").GetString() == "Manager");
        Assert.Empty(mgrGrant.GetProperty("permissions").EnumerateArray());
    }

    [Fact]
    public async Task Put_role_permissions_with_invalid_permission_returns_400()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);

        var response = await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = new[] { "FlyAway" } });
        Assert.Equal(400, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid permission(s)", body.GetProperty("title").GetString());
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
                    ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-production-use-32b",
                });
            });
        });
    }

    private static async Task<string> GetDevTokenAsync(HttpClient client, Guid tenantId, Guid branchId, Guid userId)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId,
            branchId,
            userId,
            role = "Manager",
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedTenantAsync(Guid tenantId, Guid branchId, Guid userId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')", connection))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("name", $"Tenant {tenantId:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", branchId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Test Branch");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, display_name, role, is_active) VALUES (@id, @tenantId, @name, @role, true)", connection))
        {
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Test Manager");
            cmd.Parameters.AddWithValue("role", "Manager");
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task SeedUserAsync(Guid tenantId, Guid userId, string displayName, string role)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, display_name, role, is_active) VALUES (@id, @tenantId, @name, @role, true)", connection))
        {
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", displayName);
            cmd.Parameters.AddWithValue("role", role);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
