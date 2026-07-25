using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests.Auth;

[Collection(RlsCollection.Name)]
public sealed class PermissionEnforcementTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;
    private const string CategoryName = "Main";

    [Fact]
    public async Task Waiter_without_grants_cannot_create_user()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId);

        var response = await client.PostAsJsonAsync("/users", new { displayName = "Hacker", role = "Manager" });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Waiter_without_grants_cannot_set_permissions()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId);

        var response = await client.PutAsJsonAsync("/roles/Manager/permissions", new { permissions = new[] { "VoidLine" } });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Cashier_without_EditMenu_cannot_create_menu_item()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Cashier");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId);
        var categoryId = await SeedCategoryAsync(tenantId);
        var stationId = await SeedStationAsync(tenantId, branchId);

        var response = await client.PostAsJsonAsync("/menu-items", new
        {
            categoryId,
            stationId,
            name = "Burger",
            price = 10m,
            availability = "Available",
        });
        Assert.Equal(403, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Owner_bypasses_permission_matrix_for_menu_item_create()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Owner");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId);
        var categoryId = await SeedCategoryAsync(tenantId);
        var stationId = await SeedStationAsync(tenantId, branchId);

        var response = await client.PostAsJsonAsync("/menu-items", new
        {
            categoryId,
            stationId,
            name = "Burger",
            price = 10m,
            availability = "Available",
        });
        Assert.Equal(201, (int)response.StatusCode);
    }

    [Fact]
    public async Task Waiter_without_CloseOrder_cannot_close_order()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);

        // Grant Waiter the operational permissions needed for setup (create, fire, bump, serve)
        await using (var grantConn = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await grantConn.OpenAsync();
            await using var grantTx = await grantConn.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(grantConn, tenantId);
            foreach (var perm in new[] { "TakeOrder", "FireOrder", "BumpKitchenLine", "ServeLine" })
            {
                await using var cmd = new NpgsqlCommand(
                    "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", grantConn);
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("role", "Waiter");
                cmd.Parameters.AddWithValue("permission", perm);
                await cmd.ExecuteNonQueryAsync();
            }
            await grantTx.CommitAsync();
        }

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        // Serve
        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();
        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        Assert.Equal(403, (int)closeResponse.StatusCode);

        var errorBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", errorBody.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Cashier_with_CloseOrder_grant_can_close_order()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, cashierId, "Cashier");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);

        // Insert a cashier user in the DB (FK on orders.waiter_id references users.id)
        await using (var userConn = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await userConn.OpenAsync();
            await using var userTx = await userConn.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(userConn, tenantId);
            await using var userCmd = new NpgsqlCommand(
                "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @name, 'Cashier')", userConn);
            userCmd.Parameters.AddWithValue("id", cashierId);
            userCmd.Parameters.AddWithValue("tenantId", tenantId);
            userCmd.Parameters.AddWithValue("name", "Test Cashier");
            await userCmd.ExecuteNonQueryAsync();
            await userTx.CommitAsync();
        }

        // Seed CloseOrder grant for Cashier
        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(connection, tenantId);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("role", "Cashier");
            cmd.Parameters.AddWithValue("permission", "CloseOrder");
            await cmd.ExecuteNonQueryAsync();

            // Grant Cashier the operational permissions needed for setup (create, fire, bump, serve)
            foreach (var perm in new[] { "TakeOrder", "FireOrder", "BumpKitchenLine", "ServeLine" })
            {
                await using var opCmd = new NpgsqlCommand(
                    "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
                opCmd.Parameters.AddWithValue("id", Guid.NewGuid());
                opCmd.Parameters.AddWithValue("tenantId", tenantId);
                opCmd.Parameters.AddWithValue("role", "Cashier");
                opCmd.Parameters.AddWithValue("permission", perm);
                await opCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();
        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Owner_bypasses_all_permission_families()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Owner");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);

        // EditMenu: PUT /menu-items
        var editResponse = await client.PutAsJsonAsync($"/menu-items/{menuItemId}", new
        {
            categoryId,
            stationId,
            name = "Updated Burger",
            price = 12m,
            availability = "Available",
        });
        Assert.Equal(200, (int)editResponse.StatusCode);

        // ManageFloor: POST /sections
        var sectionResponse = await client.PostAsJsonAsync("/sections", new { name = "New Section" });
        Assert.Equal(201, (int)sectionResponse.StatusCode);

        // ManageStaff: GET /users
        var usersResponse = await client.GetAsync("/users");
        Assert.Equal(200, (int)usersResponse.StatusCode);

        // CloseOrder: close (need to create, serve, then close)
        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();
        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        Assert.Equal(200, (int)closeResponse.StatusCode);

        // ViewHistory: GET /orders/history
        var historyResponse = await client.GetAsync("/orders/history");
        Assert.Equal(200, (int)historyResponse.StatusCode);

        // ViewHistory: GET /dashboard/summary
        var dashboardResponse = await client.GetAsync("/dashboard/summary");
        Assert.Equal(200, (int)dashboardResponse.StatusCode);
    }

    [Fact]
    public async Task Role_with_seeded_EditMenu_grant_can_create_category()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId);

        // Seed EditMenu grant for Manager
        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(connection, tenantId);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("role", "Manager");
            cmd.Parameters.AddWithValue("permission", "EditMenu");
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var response = await client.PostAsJsonAsync("/categories", new { name = "New Category", sortOrder = 1 });
        Assert.Equal(201, (int)response.StatusCode);
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

    private async Task SeedTenantAsync(Guid tenantId, Guid branchId)
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

        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedCategoryAsync(Guid tenantId)
    {
        var categoryId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, @name, 1)", connection))
        {
            cmd.Parameters.AddWithValue("id", categoryId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", CategoryName);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return categoryId;
    }

    private async Task<Guid> SeedStationAsync(Guid tenantId, Guid branchId)
    {
        var stationId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", stationId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", StationName);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return stationId;
    }

    private async Task SeedFullAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId,
        Guid stationId, Guid categoryId, Guid menuItemId)
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
            "INSERT INTO branch_settings (id, tenant_id, branch_id, currency, tax_rate, tax_applies, service_charge_rate, service_applies, tax_on_service) VALUES (@id, @tenantId, @branchId, 'EGP', 0.14, true, 0.12, true, false)", connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @name, 'Waiter')", connection))
        {
            cmd.Parameters.AddWithValue("id", waiterId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Test Waiter");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", sectionId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", "Terrace");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", tableId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", "12");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", stationId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", StationName);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, @name, 1)", connection))
        {
            cmd.Parameters.AddWithValue("id", categoryId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", CategoryName);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, @name, @price, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", menuItemId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("categoryId", categoryId);
            cmd.Parameters.AddWithValue("stationId", stationId);
            cmd.Parameters.AddWithValue("name", MenuItemName);
            cmd.Parameters.AddWithValue("price", MenuItemPrice);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
