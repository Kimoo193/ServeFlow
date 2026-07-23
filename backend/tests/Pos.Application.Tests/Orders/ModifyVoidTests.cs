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
public sealed class ModifyVoidTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;

    [Fact]
    public async Task Void_fired_line_as_own_waiter_succeeds()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var line = getBody.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Fired", line.GetProperty("status").GetString());
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var voidResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/void", new { reason = "Customer changed mind", rowVersion });
        voidResponse.EnsureSuccessStatusCode();
        var voidBody = await voidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Voided", voidBody.GetProperty("status").GetString());
        Assert.Equal(lineId, voidBody.GetProperty("lineId").GetGuid());

        var get2 = await client.GetAsync($"/orders/{orderId}");
        get2.EnsureSuccessStatusCode();
        var get2Body = await get2.Content.ReadFromJsonAsync<JsonElement>();
        var line2 = get2Body.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Voided", line2.GetProperty("status").GetString());

        Assert.True(await OrderEventWithWasteFlagAsync(fixture.AppConnectionString, tenantId, orderId, "Voided", false));
    }

    [Fact]
    public async Task Void_fired_line_as_different_waiter_returns_403()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var otherWaiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var waiterToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", waiterToken);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

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

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var otherToken = await GetDevTokenAsync(client, tenantId, branchId, otherWaiterId, "Waiter");
        using var voidRequest = new HttpRequestMessage(HttpMethod.Post, $"/orders/{orderId}/lines/{lineId}/void");
        voidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        voidRequest.Content = JsonContent.Create(new { reason = "Test void", rowVersion });
        using var voidResponse = await client.SendAsync(voidRequest);
        Assert.Equal(403, (int)voidResponse.StatusCode);
    }

    [Fact]
    public async Task Void_preparing_line_as_waiter_returns_403_as_manager_succeeds_with_waste_flag()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var managerToken = await GetDevTokenAsync(client, tenantId, branchId, managerId, "Manager");
        var waiterToken = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", waiterToken);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(connection, tenantId);
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @name, 'Manager')", connection))
            {
                cmd.Parameters.AddWithValue("id", managerId);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("name", "Test Manager");
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection))
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("role", "Manager");
                cmd.Parameters.AddWithValue("permission", "VoidLine");
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
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

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var line = getBody.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Preparing", line.GetProperty("status").GetString());
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var waiterVoidResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/void", new { reason = "Waiter void attempt", rowVersion });
        Assert.Equal(403, (int)waiterVoidResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var managerVoidResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/void", new { reason = "Quality issue", rowVersion });
        managerVoidResponse.EnsureSuccessStatusCode();
        var voidBody = await managerVoidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Voided", voidBody.GetProperty("status").GetString());

        Assert.True(await OrderEventWithWasteFlagAsync(fixture.AppConnectionString, tenantId, orderId, "Voided", true));
    }

    [Fact]
    public async Task Void_without_reason_returns_400()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

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

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var voidResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/void", new { reason = "", rowVersion });
        Assert.Equal(400, (int)voidResponse.StatusCode);
    }

    [Fact]
    public async Task Increase_quantity_on_fired_line_as_own_waiter_succeeds()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        var originalFireBatchId = fireBody.GetProperty("fireBatchId").GetGuid();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var delta = 3;
        var incResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/increase-quantity", new { quantityDelta = delta, rowVersion });
        incResponse.EnsureSuccessStatusCode();
        var incBody = await incResponse.Content.ReadFromJsonAsync<JsonElement>();
        var newLineId = incBody.GetProperty("lineId").GetGuid();
        var newFireBatchId = incBody.GetProperty("fireBatchId").GetGuid();
        Assert.Equal(delta, incBody.GetProperty("quantity").GetInt32());
        Assert.Equal(MenuItemPrice, incBody.GetProperty("unitPrice").GetDecimal());
        Assert.NotEqual(originalFireBatchId, newFireBatchId);

        var get2 = await client.GetAsync($"/orders/{orderId}");
        get2.EnsureSuccessStatusCode();
        var get2Body = await get2.Content.ReadFromJsonAsync<JsonElement>();
        var lines2 = get2Body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines2.Count);

        var line0 = lines2[0];
        Assert.Equal(lineId, line0.GetProperty("id").GetGuid());
        Assert.Equal("Fired", line0.GetProperty("status").GetString());
        Assert.Equal(2, line0.GetProperty("quantity").GetInt32());

        var line1 = lines2[1];
        Assert.Equal(newLineId, line1.GetProperty("id").GetGuid());
        Assert.Equal("Fired", line1.GetProperty("status").GetString());
        Assert.Equal(delta, line1.GetProperty("quantity").GetInt32());
        Assert.Equal(newFireBatchId, line1.GetProperty("fireBatchId").GetGuid());
        Assert.Equal(line0.GetProperty("itemId").GetGuid(), line1.GetProperty("itemId").GetGuid());
        Assert.Equal(line0.GetProperty("stationId").GetGuid(), line1.GetProperty("stationId").GetGuid());

        Assert.True(await OrderEventWithOrderLineIdAsync(fixture.AppConnectionString, tenantId, orderId, "QuantityChanged", lineId));
    }

    [Fact]
    public async Task Increase_quantity_bills_added_units_at_original_line_price_not_current_menu_price()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Fire a line at the original menu price (MenuItemPrice = 15.50).
        var lineBody = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        // The menu price is raised AFTER the line was fired.
        const decimal raisedPrice = 20.00m;
        await using (var connection = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(connection, tenantId);
            await using var cmd = new NpgsqlCommand("UPDATE menu_items SET price = @price WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("price", raisedPrice);
            cmd.Parameters.AddWithValue("id", menuItemId);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        // Increasing quantity must bill the added units at the ORIGINAL snapshot price, not 20.00.
        var incResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/increase-quantity", new { quantityDelta = 3, rowVersion });
        incResponse.EnsureSuccessStatusCode();
        var incBody = await incResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MenuItemPrice, incBody.GetProperty("unitPrice").GetDecimal());
        Assert.NotEqual(raisedPrice, incBody.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task Increase_quantity_on_preparing_line_as_waiter_returns_403()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

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

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var incResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/increase-quantity", new { quantityDelta = 1, rowVersion });
        Assert.Equal(403, (int)incResponse.StatusCode);
    }

    [Fact]
    public async Task Increase_quantity_on_ready_line_returns_409()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId, "Waiter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

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

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var incResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/increase-quantity", new { quantityDelta = 1, rowVersion });
        Assert.Equal(409, (int)incResponse.StatusCode);
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

    private async Task SeedAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId,
        Guid stationId, Guid menuItemId)
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

        var categoryId = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, @name, 1)", connection))
        {
            cmd.Parameters.AddWithValue("id", categoryId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Main");
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

    private static async Task<bool> OrderEventWithWasteFlagAsync(string connectionString, Guid tenantId, Guid orderId, string eventType, bool expectedWasteFlag)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT details FROM order_events WHERE order_id = @orderId AND event_type = @eventType::text ORDER BY performed_at DESC LIMIT 1", connection);
        cmd.Parameters.AddWithValue("orderId", orderId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null) return false;

        var details = JsonSerializer.Deserialize<JsonElement>((string)result);
        return details.GetProperty("wasteFlag").GetBoolean() == expectedWasteFlag;
    }

    private static async Task<bool> OrderEventWithOrderLineIdAsync(string connectionString, Guid tenantId, Guid orderId, string eventType, Guid orderLineId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM order_events WHERE order_id = @orderId AND event_type = @eventType::text AND order_line_id = @orderLineId LIMIT 1", connection);
        cmd.Parameters.AddWithValue("orderId", orderId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("orderLineId", orderLineId);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }
}
