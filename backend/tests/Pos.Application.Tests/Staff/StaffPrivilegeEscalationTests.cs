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
public sealed class StaffPrivilegeEscalationTests(PostgresRlsFixture fixture)
{
    [Fact]
    public async Task Manager_self_escalation_to_Owner_is_blocked()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync($"/users/{managerId}", new { displayName = "Elevated", role = "Owner", isActive = true });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Manager_cannot_create_Owner()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PostAsJsonAsync("/users", new { displayName = "FakeOwner", role = "Owner" });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Owner_can_create_another_Owner()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, ownerId, "Owner");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, ownerId, "Owner");

        var response = await client.PostAsJsonAsync("/users", new { displayName = "SecondOwner", role = "Owner" });
        Assert.Equal(201, (int)response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SecondOwner", created.GetProperty("displayName").GetString());
        Assert.Equal("Owner", created.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Manager_cannot_deactivate_themselves()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync($"/users/{managerId}", new { displayName = "Deactivated", role = "Manager", isActive = false });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Demoting_only_active_Owner_is_blocked()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedUserAsync(tenantId, ownerId, "SoleOwner", "Owner");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync($"/users/{ownerId}", new { displayName = "SoleOwner", role = "Cashier", isActive = true });
        Assert.Equal(409, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Last Owner", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Demoting_an_Owner_when_another_active_Owner_exists_succeeds()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var ownerToDemote = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedUserAsync(tenantId, ownerToDemote, "OwnerToDemote", "Owner");
        await SeedUserAsync(tenantId, otherOwner, "OtherOwner", "Owner");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync($"/users/{ownerToDemote}", new { displayName = "OwnerToDemote", role = "Cashier", isActive = true });
        Assert.Equal(200, (int)response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cashier", result.GetProperty("role").GetString());
    }

    [Fact]
    public async Task NonOwner_cannot_grant_unheld_permission()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = new[] { "VoidLine" } });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    /// <summary>
    /// Guard C's "does not over-block" case, and the only test that pins it down.
    /// </summary>
    /// <remarks>
    /// Without this, Guard C could be tightened to "only an Owner may ever edit the permission
    /// matrix" and the whole suite would still pass: the two negative tests here expect 403
    /// either way, and StaffAdminTests' success-path test calls /dev/token with no role, so it
    /// runs as Owner and bypasses the guard entirely. This asserts the case that actually
    /// distinguishes the two rules — a NON-Owner granting a permission it does hold.
    /// </remarks>
    [Fact]
    public async Task NonOwner_can_grant_a_permission_it_does_hold()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");
        await SeedRolePermissionAsync(tenantId, "Manager", "VoidLine");

        // Manager holds VoidLine, so granting it to Waiter must succeed.
        var response = await client.PutAsJsonAsync("/roles/Waiter/permissions", new { permissions = new[] { "VoidLine" } });
        Assert.Equal(200, (int)response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Waiter", result.GetProperty("role").GetString());
        Assert.Equal(1, result.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task Owner_role_permissions_are_not_editable()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var response = await client.PutAsJsonAsync("/roles/Owner/permissions", new { permissions = new[] { "VoidLine" } });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Manager_creates_Waiter_then_changes_role_to_Cashier_succeeds()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantWithRoleAsync(tenantId, branchId, managerId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Manager", "ManageStaff");

        var postResponse = await client.PostAsJsonAsync("/users", new { displayName = "Terry", role = "Waiter" });
        Assert.Equal(201, (int)postResponse.StatusCode);
        var created = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var terryId = created.GetProperty("id").GetGuid();

        var putResponse = await client.PutAsJsonAsync($"/users/{terryId}", new { displayName = "Terry", role = "Cashier", isActive = true });
        Assert.Equal(200, (int)putResponse.StatusCode);
        var updated = await putResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cashier", updated.GetProperty("role").GetString());
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

    private static async Task<string> GetDevTokenAsync(HttpClient client, Guid tenantId, Guid branchId, Guid userId, string role)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId,
            branchId,
            userId,
            role,
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedTenantWithRoleAsync(Guid tenantId, Guid branchId, Guid userId, string role)
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
            cmd.Parameters.AddWithValue("name", $"User {userId:N}"[..10]);
            cmd.Parameters.AddWithValue("role", role);
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

    private async Task SeedRolePermissionAsync(Guid tenantId, string role, string permission)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("permission", permission);
        await cmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
