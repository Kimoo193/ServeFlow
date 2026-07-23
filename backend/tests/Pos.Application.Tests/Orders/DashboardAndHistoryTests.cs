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
public sealed class DashboardAndHistoryTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;

    [Fact]
    public async Task Dashboard_returns_todays_order_count()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItemId) = await SeedMinimalAsync();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create 3 dine-in orders today
        for (var i = 0; i < 3; i++)
        {
            var tableIdForOrder = i == 0 ? tableId : await SeedTableAsync(fixture.AppConnectionString, tenantId, sectionId);
            var response = await client.PostAsync($"/tables/{tableIdForOrder}/orders", null);
            response.EnsureSuccessStatusCode();
        }

        // Assert dashboard counts
        var summaryResponse = await client.GetAsync("/dashboard/summary");
        summaryResponse.EnsureSuccessStatusCode();
        var body = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, body.GetProperty("ordersToday").GetInt32());
        Assert.Equal(3, body.GetProperty("openNow").GetInt32());
    }

    [Fact]
    public async Task Dashboard_excludes_old_created_events()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItemId) = await SeedMinimalAsync();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create 2 dine-in orders today
        var response1 = await client.PostAsync($"/tables/{tableId}/orders", null);
        response1.EnsureSuccessStatusCode();
        var o1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var orderId1 = o1.GetProperty("orderId").GetGuid();

        var table2Id = await SeedTableAsync(fixture.AppConnectionString, tenantId, sectionId);
        var response2 = await client.PostAsync($"/tables/{table2Id}/orders", null);
        response2.EnsureSuccessStatusCode();

        // Assert 2 today
        var summaryResponse = await client.GetAsync("/dashboard/summary");
        summaryResponse.EnsureSuccessStatusCode();
        var body = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("ordersToday").GetInt32());

        // Raw SQL: insert an old order with a Created event with PerformedAt 3 days ago
        var oldOrderId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO orders (id, tenant_id, branch_id, waiter_id, order_type, status, row_version) VALUES (@id, @tenantId, @branchId, @waiterId, 'DineIn', 'Completed', 1)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", oldOrderId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("waiterId", waiterId);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO order_events (id, tenant_id, order_id, event_type, details, performed_by, performed_at) VALUES (@id, @tenantId, @orderId, 'Created', '{}', @by, @at)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("orderId", oldOrderId);
            cmd.Parameters.AddWithValue("by", waiterId);
            cmd.Parameters.AddWithValue("at", DateTime.UtcNow.AddDays(-3));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        // Assert still 2 — old event is excluded
        var summary2 = await client.GetAsync("/dashboard/summary");
        summary2.EnsureSuccessStatusCode();
        var body2 = await summary2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body2.GetProperty("ordersToday").GetInt32());
    }

    [Fact]
    public async Task History_returns_orders_newest_first()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItemId) = await SeedMinimalAsync();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create order 1 (older, will remain open)
        var order1Response = await client.PostAsync($"/tables/{tableId}/orders", null);
        order1Response.EnsureSuccessStatusCode();
        var order1Body = await order1Response.Content.ReadFromJsonAsync<JsonElement>();
        var order1Id = order1Body.GetProperty("orderId").GetGuid();

        // Add a line, fire, serve, close order 2 (newer, completed)
        var table2Id = await SeedTableAsync(fixture.AppConnectionString, tenantId, sectionId);
        var order2Response = await client.PostAsync($"/tables/{table2Id}/orders", null);
        order2Response.EnsureSuccessStatusCode();
        var order2Body = await order2Response.Content.ReadFromJsonAsync<JsonElement>();
        var order2Id = order2Body.GetProperty("orderId").GetGuid();
        var rowVersion2 = order2Body.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion = rowVersion2 };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{order2Id}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var line2Id = lineResp.GetProperty("lineId").GetGuid();
        rowVersion2 = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{order2Id}/fire", new { rowVersion = rowVersion2 });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion2 = fireBody.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, order2Id, line2Id);

        var closeResponse = await client.PostAsJsonAsync($"/orders/{order2Id}/close", new { rowVersion = rowVersion2 });
        closeResponse.EnsureSuccessStatusCode();

        // GET /orders/history — newest first (order2 then order1)
        var historyResponse = await client.GetAsync("/orders/history");
        historyResponse.EnsureSuccessStatusCode();
        var historyBody = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orders = historyBody.EnumerateArray().ToList();

        Assert.Equal(2, orders.Count);

        // Order2 (completed) is first (newest)
        var firstOrder = orders[0];
        Assert.Equal(order2Id, firstOrder.GetProperty("id").GetGuid());
        Assert.Equal("Completed", firstOrder.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Number, firstOrder.GetProperty("total").ValueKind);
        Assert.NotNull(firstOrder.GetProperty("completedAt").GetString());
        Assert.Equal(31.00m, firstOrder.GetProperty("total").GetDecimal());

        // Order1 (open) is second
        var secondOrder = orders[1];
        Assert.Equal(order1Id, secondOrder.GetProperty("id").GetGuid());
        Assert.Equal("Open", secondOrder.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, secondOrder.GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.Null, secondOrder.GetProperty("completedAt").ValueKind);
        Assert.NotNull(secondOrder.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task History_respects_limit()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItemId) = await SeedMinimalAsync();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create 3 orders
        for (var i = 0; i < 3; i++)
        {
            var tId = i == 0 ? tableId : await SeedTableAsync(fixture.AppConnectionString, tenantId, sectionId);
            var response = await client.PostAsync($"/tables/{tId}/orders", null);
            response.EnsureSuccessStatusCode();
        }

        // limit=1 returns only the newest
        var historyResponse = await client.GetAsync("/orders/history?limit=1");
        historyResponse.EnsureSuccessStatusCode();
        var historyBody = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orders = historyBody.EnumerateArray().ToList();

        Assert.Single(orders);
    }

    // --- Infrastructure ---

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

    private static async Task ServeLineAsync(HttpClient client, Guid orderId, Guid lineId)
    {
        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();

        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();

        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();
    }

    private async Task<(Guid tenantId, Guid branchId, Guid sectionId, Guid tableId, Guid waiterId, Guid stationId, Guid menuItemId)> SeedMinimalAsync()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

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

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, @name, 1)", connection))
        {
            cmd.Parameters.AddWithValue("id", categoryId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Main");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, @name, @price, 'Available')",
            connection))
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

        return (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItemId);
    }

    private static async Task<Guid> SeedTableAsync(string connectionString, Guid tenantId, Guid sectionId)
    {
        var tableId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')",
            connection))
        {
            cmd.Parameters.AddWithValue("id", tableId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", tableId.ToString("N")[..4]);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return tableId;
    }
}
