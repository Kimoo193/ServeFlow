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
public sealed class OfflineIdempotencyTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;

    [Fact]
    public async Task Same_client_orderId_twice_returns_same_order_and_does_not_duplicate()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var clientOrderId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        // First call — supply a client-chosen OrderId
        var body1 = new { orderId = clientOrderId };
        var response1 = await client.PostAsJsonAsync($"/tables/{tableId}/orders", body1);
        response1.EnsureSuccessStatusCode();
        var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(clientOrderId, result1.GetProperty("orderId").GetGuid());

        // Second call — same OrderId, should return the same order (idempotent replay)
        var response2 = await client.PostAsJsonAsync($"/tables/{tableId}/orders", body1);
        response2.EnsureSuccessStatusCode();
        var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(clientOrderId, result2.GetProperty("orderId").GetGuid());

        // Verify only ONE order exists for this table via direct DB query
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM orders WHERE table_id = @tableId", connection);
        countCmd.Parameters.AddWithValue("tableId", tableId);
        var count = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Same_client_lineId_twice_returns_same_line_and_does_not_duplicate()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var clientLineId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        // Open an order (no idempotency key needed for the order itself here)
        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // First add-line call — supply a client-chosen LineId
        var lineBody1 = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion, lineId = clientLineId };
        var response1 = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody1);
        response1.EnsureSuccessStatusCode();
        var result1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = result1.GetProperty("rowVersion").GetInt32();

        Assert.Equal(clientLineId, result1.GetProperty("lineId").GetGuid());

        // Second call — same LineId, same orderId, but stale rowVersion (simulating offline replay)
        var staleRowVersion = rowVersion - 1;
        var lineBody2 = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion = staleRowVersion, lineId = clientLineId };
        var response2 = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody2);
        response2.EnsureSuccessStatusCode();
        var result2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(clientLineId, result2.GetProperty("lineId").GetGuid());
        Assert.Equal(result1.GetProperty("sequence").GetInt32(), result2.GetProperty("sequence").GetInt32());
        Assert.Equal(result1.GetProperty("unitPrice").GetDecimal(), result2.GetProperty("unitPrice").GetDecimal());

        // Verify only ONE line exists via GET /orders/{orderId}
        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lines = getBody.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
    }

    [Fact]
    public async Task Dine_in_replay_checks_table_and_returns_409_on_conflict()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableAId = Guid.NewGuid();
        var tableBId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var clientOrderId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableAId, stationId, menuItemId);

        // Seed a second table
        await using (var conn = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(conn, tenantId);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')", conn);
            cmd.Parameters.AddWithValue("id", tableBId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", "14");
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // First call on table A with client-chosen OrderId
        var body = new { orderId = clientOrderId };
        var responseA = await client.PostAsJsonAsync($"/tables/{tableAId}/orders", body);
        responseA.EnsureSuccessStatusCode();
        var resultA = await responseA.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientOrderId, resultA.GetProperty("orderId").GetGuid());

        // Same OrderId on table B — must return 409 (conflict, not replay)
        var responseB = await client.PostAsJsonAsync($"/tables/{tableBId}/orders", body);
        Assert.Equal(409, (int)responseB.StatusCode);
        var errorBody = await responseB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order id conflict", errorBody.GetProperty("title").GetString());

        // Same OrderId on table A — still replays (idempotent)
        var responseA2 = await client.PostAsJsonAsync($"/tables/{tableAId}/orders", body);
        responseA2.EnsureSuccessStatusCode();
        var resultA2 = await responseA2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientOrderId, resultA2.GetProperty("orderId").GetGuid());
        Assert.Equal(resultA.GetProperty("rowVersion").GetInt32(), resultA2.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Stale_rowVersion_with_existing_lineId_succeeds_not_409()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var clientLineId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        // Open order
        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // First call succeeds
        var lineBody1 = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion, lineId = clientLineId };
        var response1 = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody1);
        response1.EnsureSuccessStatusCode();

        // Second call with the same LineId but deliberately stale rowVersion
        var lineBody2 = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion = 0, lineId = clientLineId };
        var response2 = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody2);
        // Must succeed (not 409) because idempotency check runs before row_version check
        response2.EnsureSuccessStatusCode();
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

    private async Task<string> GetDevTokenAsync(HttpClient client, Guid tenantId, Guid branchId, Guid userId)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId,
            branchId,
            userId,
            role = "Waiter",
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
}
