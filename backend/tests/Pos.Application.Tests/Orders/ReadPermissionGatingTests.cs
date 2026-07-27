using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests.Orders;

[Collection(RlsCollection.Name)]
public sealed class ReadPermissionGatingTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;
    private const string CategoryName = "Main";

    [Fact]
    public async Task Kitchen_with_only_BumpKitchenLine_gets_403_from_receipt_and_bill_preview()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var kitchenId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, kitchenId, "Kitchen");
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Kitchen", "BumpKitchenLine");

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        // Setup: create order, add line, fire, serve, close as Manager
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

        // Switch to Kitchen (BumpKitchenLine only)
        var kitchenToken = await GetDevTokenAsync(client, tenantId, branchId, kitchenId, "Kitchen");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kitchenToken);

        var receiptResp = await client.GetAsync($"/orders/{orderId}/receipt");
        Assert.Equal(HttpStatusCode.Forbidden, receiptResp.StatusCode);
        var receiptError = await receiptResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", receiptError.GetProperty("title").GetString());

        var previewResp = await client.GetAsync($"/orders/{orderId}/bill-preview");
        Assert.Equal(HttpStatusCode.Forbidden, previewResp.StatusCode);
        var previewError = await previewResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", previewError.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Kitchen_with_BumpKitchenLine_can_read_KDS_tickets()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var kitchenId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, kitchenId, "Kitchen");
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Kitchen", "BumpKitchenLine");

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        // Setup: create order, add line, fire (line must be Fired to appear on KDS)
        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineResp = await client.PostAsJsonAsync($"/orders/{orderId}/lines",
            new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        lineResp.EnsureSuccessStatusCode();
        rowVersion = (await lineResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        var fireResp = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResp.EnsureSuccessStatusCode();

        // Switch to Kitchen
        var kitchenToken = await GetDevTokenAsync(client, tenantId, branchId, kitchenId, "Kitchen");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kitchenToken);

        var ticketsResp = await client.GetAsync($"/kds/stations/{stationId}/tickets");
        Assert.Equal(HttpStatusCode.OK, ticketsResp.StatusCode);

        var ticketsBody = await ticketsResp.Content.ReadFromJsonAsync<JsonElement>();
        var tickets = ticketsBody.EnumerateArray().ToList();
        Assert.NotEmpty(tickets);
        Assert.Equal(orderId, tickets[0].GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task Cashier_with_CloseOrder_gets_200_from_orders_open_and_bill_preview()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, cashierId, "Cashier");
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Cashier", "CloseOrder");
        await SeedRolePermissionAsync(tenantId, "Cashier", "TakeOrder");

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        // Setup: open order with a line (so orders/open returns it)
        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineResp = await client.PostAsJsonAsync($"/orders/{orderId}/lines",
            new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion });
        lineResp.EnsureSuccessStatusCode();
        rowVersion = (await lineResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        // Switch to Cashier
        var cashierToken = await GetDevTokenAsync(client, tenantId, branchId, cashierId, "Cashier");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashierToken);

        var openResp = await client.GetAsync("/orders/open");
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var openBody = await openResp.Content.ReadFromJsonAsync<JsonElement>();
        var openOrders = openBody.EnumerateArray().ToList();
        Assert.Contains(openOrders, o => o.GetProperty("id").GetGuid() == orderId);

        var previewResp = await client.GetAsync($"/orders/{orderId}/bill-preview");
        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
    }

    [Fact]
    public async Task Principal_without_relevant_permissions_gets_403_from_orders_open()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var kitchenId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);
        await SeedUserAsync(tenantId, kitchenId, "Kitchen");
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");
        await SeedRolePermissionAsync(tenantId, "Kitchen", "BumpKitchenLine");

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        // Open an order so there is something to read
        var orderResp = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResp.EnsureSuccessStatusCode();

        // Switch to Kitchen (only BumpKitchenLine — no CloseOrder, no TakeOrder)
        var kitchenToken = await GetDevTokenAsync(client, tenantId, branchId, kitchenId, "Kitchen");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kitchenToken);

        var openResp = await client.GetAsync("/orders/open");
        Assert.Equal(HttpStatusCode.Forbidden, openResp.StatusCode);

        var errorBody = await openResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", errorBody.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(5, true)]
    public async Task Combo_component_quantity_is_bounded_1_to_99(int quantity, bool shouldSucceed)
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var comboId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, Guid.NewGuid(), "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        await SeedComboBasicsAsync(tenantId, branchId, stationId, categoryId, menuItemId, comboId);
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");

        var response = await client.PostAsJsonAsync($"/combos/{comboId}/items/{menuItemId}",
            new { quantity });

        if (shouldSucceed)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Invalid quantity", error.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task Combo_expanding_to_more_than_50_lines_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var comboId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Manager");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        await SeedTenantBranchAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId);
        await SeedAllPermissionsForRoleAsync(tenantId, "Manager");

        var categoryId = Guid.NewGuid();
        await SeedCategoryAsync(tenantId, categoryId);

        // Create 51 distinct menu items on the same station
        var itemIds = new List<Guid>();
        for (var i = 0; i < 51; i++)
        {
            var itemId = Guid.NewGuid();
            itemIds.Add(itemId);
            await SeedMenuItemAsync(tenantId, categoryId, stationId, itemId, $"Item {i}", 10.00m);
        }

        // Create a combo
        await SeedComboAsync(tenantId, comboId, "Mega Combo", 500.00m);

        // Attach all 51 items as components with quantity 1 each (total = 51 > 50)
        for (var i = 0; i < 51; i++)
        {
            var attachResp = await client.PostAsJsonAsync($"/combos/{comboId}/items/{itemIds[i]}",
                new { quantity = 1 });
            attachResp.EnsureSuccessStatusCode();
        }

        // Open an order
        var orderResp = await client.PostAsJsonAsync($"/tables/{tableId}/orders", new { });
        orderResp.EnsureSuccessStatusCode();
        var orderBody = await orderResp.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Try to add the combo — should be rejected (51 lines > 50)
        var comboResp = await client.PostAsJsonAsync($"/orders/{orderId}/combos",
            new { comboId, rowVersion });

        Assert.Equal(HttpStatusCode.BadRequest, comboResp.StatusCode);
        var error = await comboResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Combo too large", error.GetProperty("title").GetString());
    }

    // --- harness ----------------------------------------------------------

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

    private async Task SeedComboBasicsAsync(
        Guid tenantId, Guid branchId, Guid stationId,
        Guid categoryId, Guid menuItemId, Guid comboId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        await ExecAsync("INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')",
            ("id", tenantId), ("name", $"Tenant {tenantId:N}"));

        await ExecAsync("INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, 'Test Branch')",
            ("id", branchId), ("tenantId", tenantId));

        await ExecAsync("INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Grill')",
            ("id", stationId), ("tenantId", tenantId), ("branchId", branchId));

        await ExecAsync("INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, 'Main', 1)",
            ("id", categoryId), ("tenantId", tenantId));

        await ExecAsync("INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, 'Burger', @price, 'Available')",
            ("id", menuItemId), ("tenantId", tenantId), ("categoryId", categoryId), ("stationId", stationId), ("price", 8.00m));

        await ExecAsync("INSERT INTO combos (id, tenant_id, name, price) VALUES (@id, @tenantId, 'Lunch Deal', 9.99)",
            ("id", comboId), ("tenantId", tenantId));

        await transaction.CommitAsync();
    }

    private async Task SeedTenantBranchAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId, Guid stationId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        await ExecAsync("INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')",
            ("id", tenantId), ("name", $"Tenant {tenantId:N}"));

        await ExecAsync("INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, 'Test Branch')",
            ("id", branchId), ("tenantId", tenantId));

        await ExecAsync("INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, 'Test Waiter', 'Waiter')",
            ("id", waiterId), ("tenantId", tenantId));

        await ExecAsync("INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Terrace')",
            ("id", sectionId), ("tenantId", tenantId), ("branchId", branchId));

        await ExecAsync("INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, '12', 'Available')",
            ("id", tableId), ("tenantId", tenantId), ("sectionId", sectionId));

        await ExecAsync("INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Grill')",
            ("id", stationId), ("tenantId", tenantId), ("branchId", branchId));

        await transaction.CommitAsync();
    }

    private async Task SeedCategoryAsync(Guid tenantId, Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, 'Main', 1)", connection);
        cmd.Parameters.AddWithValue("id", categoryId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        await cmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    private async Task SeedMenuItemAsync(
        Guid tenantId, Guid categoryId, Guid stationId, Guid itemId,
        string name, decimal price)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, @name, @price, 'Available')", connection);
        cmd.Parameters.AddWithValue("id", itemId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("categoryId", categoryId);
        cmd.Parameters.AddWithValue("stationId", stationId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    private async Task SeedComboAsync(Guid tenantId, Guid comboId, string name, decimal price)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO combos (id, tenant_id, name, price) VALUES (@id, @tenantId, @name, @price)", connection);
        cmd.Parameters.AddWithValue("id", comboId);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
