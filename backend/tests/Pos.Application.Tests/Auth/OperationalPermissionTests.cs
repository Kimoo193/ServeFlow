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
public sealed class OperationalPermissionTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;
    private const string CategoryName = "Main";

    [Theory]
    [InlineData("POST", "/tables/{0}/orders", "TakeOrder", "Cashier")]
    [InlineData("POST", "/orders", "TakeOrder", "Cashier")]
    [InlineData("POST", "/orders/{0}/lines", "TakeOrder", "Cashier")]
    [InlineData("PATCH", "/orders/{0}/lines/{1}", "TakeOrder", "Cashier")]
    [InlineData("DELETE", "/orders/{0}/lines/{1}", "TakeOrder", "Cashier")]
    [InlineData("POST", "/orders/{0}/fire", "FireOrder", "Cashier")]
    [InlineData("POST", "/kds/lines/{0}/start-preparing", "BumpKitchenLine", "Waiter")]
    [InlineData("POST", "/kds/lines/{0}/ready", "BumpKitchenLine", "Waiter")]
    [InlineData("POST", "/orders/{0}/lines/{1}/serve", "ServeLine", "Cashier")]
    [InlineData("POST", "/orders/{0}/receipt/print", "ReprintReceipt", "Waiter")]
    public async Task Role_without_grant_gets_403(
        string method, string relativeUrl, string requiredPermission, string unauthorizedRole)
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
        var kitchenId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, managerId, "Manager");
        await SeedUserAsync(tenantId, cashierId, "Cashier");
        await SeedUserAsync(tenantId, kitchenId, "Kitchen");

        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");

        // Set up full prerequisite state with Manager: order -> line -> fire -> prep -> ready -> serve -> close
        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineResp = await client.PostAsJsonAsync($"/orders/{orderId}/lines",
            new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        lineResp.EnsureSuccessStatusCode();
        var lineBody = await lineResp.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineBody.GetProperty("lineId").GetGuid();
        rowVersion = lineBody.GetProperty("rowVersion").GetInt32();

        var fireResp = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResp.EnsureSuccessStatusCode();

        await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);

        var getResp = await client.GetAsync($"/orders/{orderId}");
        getResp.EnsureSuccessStatusCode();
        rowVersion = (await getResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });

        // Switch to unauthorized role
        var roleUserId = unauthorizedRole switch
        {
            "Cashier" => cashierId,
            "Waiter" => waiterId,
            "Kitchen" => kitchenId,
            _ => cashierId,
        };

        var unauthorizedToken = await GetDevTokenAsync(client, tenantId, branchId, roleUserId, unauthorizedRole);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unauthorizedToken);

        var url = relativeUrl
            .Replace("{0}", orderId.ToString())
            .Replace("{1}", lineId.ToString());

        if (method == "DELETE")
            url += "?rowVersion=1";

        using var request = BuildRequest(method, url, rowVersion, relativeUrl);
        using var response = await client.SendAsync(request);

        Assert.Equal(403, (int)response.StatusCode);

        var errorBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", errorBody.GetProperty("title").GetString());
        Assert.Contains(requiredPermission, errorBody.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Operational_flow_succeeds_with_grants()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var kitchenId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, kitchenId, "Kitchen");
        await SeedUserAsync(tenantId, cashierId, "Cashier");

        await SeedRolePermissionAsync(tenantId, "Waiter", "TakeOrder");
        await SeedRolePermissionAsync(tenantId, "Waiter", "FireOrder");
        await SeedRolePermissionAsync(tenantId, "Waiter", "ServeLine");
        await SeedRolePermissionAsync(tenantId, "Kitchen", "BumpKitchenLine");
        await SeedRolePermissionAsync(tenantId, "Cashier", "ReprintReceipt");
        await SeedRolePermissionAsync(tenantId, "Cashier", "CloseOrder");

        // Waiter: create order, add line, fire
        var waiterToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", waiterToken);

        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineResp = await client.PostAsJsonAsync($"/orders/{orderId}/lines",
            new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        lineResp.EnsureSuccessStatusCode();
        var lineBody = await lineResp.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineBody.GetProperty("lineId").GetGuid();
        rowVersion = lineBody.GetProperty("rowVersion").GetInt32();

        var fireResp = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResp.EnsureSuccessStatusCode();

        // Kitchen: bump to Preparing, then Ready
        var kitchenToken = await GetDevTokenAsync(client, tenantId, branchId, kitchenId, "Kitchen");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kitchenToken);

        var prepResp = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResp.EnsureSuccessStatusCode();
        Assert.Equal("Preparing", (await prepResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var readyResp = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResp.EnsureSuccessStatusCode();
        Assert.Equal("Ready", (await readyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Waiter: serve
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", waiterToken);
        var serveResp = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResp.EnsureSuccessStatusCode();
        Assert.Equal("Served", (await serveResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // Cashier: close then reprint receipt
        var cashierToken = await GetDevTokenAsync(client, tenantId, branchId, cashierId, "Cashier");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

        var getResp = await client.GetAsync($"/orders/{orderId}");
        getResp.EnsureSuccessStatusCode();
        rowVersion = (await getResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        var closeResp = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResp.EnsureSuccessStatusCode();

        var printResp = await client.PostAsync($"/orders/{orderId}/receipt/print", null);
        Assert.NotEqual(403, (int)printResp.StatusCode);
    }

    [Fact]
    public async Task Owner_bypasses_all_operational_permissions()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await GetDevTokenAsync(client, tenantId, branchId, ownerId, "Owner");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, ownerId, sectionId, tableId, stationId, categoryId, menuItemId);

        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineResp = await client.PostAsJsonAsync($"/orders/{orderId}/lines",
            new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        lineResp.EnsureSuccessStatusCode();
        var lineBody = await lineResp.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineBody.GetProperty("lineId").GetGuid();
        rowVersion = lineBody.GetProperty("rowVersion").GetInt32();

        var fireResp = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResp.EnsureSuccessStatusCode();

        var prepResp = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResp.EnsureSuccessStatusCode();

        var readyResp = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResp.EnsureSuccessStatusCode();

        var serveResp = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResp.EnsureSuccessStatusCode();

        var getResp = await client.GetAsync($"/orders/{orderId}");
        getResp.EnsureSuccessStatusCode();
        rowVersion = (await getResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        var closeResp = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResp.EnsureSuccessStatusCode();

        var printResp = await client.PostAsync($"/orders/{orderId}/receipt/print", null);
        Assert.NotEqual(403, (int)printResp.StatusCode);
    }

    private static HttpRequestMessage BuildRequest(string method, string url, int rowVersion, string relativeUrl)
    {
        HttpContent? content = null;

        if (method == "POST" && relativeUrl.Contains("/fire"))
            content = JsonContent.Create(new { rowVersion });
        else if (method == "POST" && relativeUrl.Contains("/lines") && relativeUrl.Contains("/orders/{0}/lines") && !relativeUrl.Contains("/serve") && !relativeUrl.Contains("/void"))
            content = JsonContent.Create(new { menuItemId = Guid.NewGuid(), quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        else if (method == "POST" && relativeUrl == "/orders")
            content = null;
        else if (method == "PATCH")
            content = JsonContent.Create(new { quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion = 1 });

        return new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
    }

    private async Task SeedAllPermissionsForRoleAsync(Guid tenantId, string role)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        foreach (var perm in Enum.GetValues<Pos.Domain.Entities.Permission>())
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("role", role);
            cmd.Parameters.AddWithValue("permission", perm.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private async Task SeedRolePermissionAsync(Guid tenantId, string role, string permission)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("permission", permission);
        await cmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    private async Task SeedUserAsync(Guid tenantId, Guid userId, string role)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @name, @role)", connection);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("name", $"Test {role}");
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
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
