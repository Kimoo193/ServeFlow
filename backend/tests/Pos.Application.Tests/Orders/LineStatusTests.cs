using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests.Orders;

[Collection(RlsCollection.Name)]
public sealed class LineStatusTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;

    [Fact]
    public async Task Full_status_round_trip()
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
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add a line
        var lineBody = new { menuItemId, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Fire
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        // Assert Fired
        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var line = getBody.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Fired", line.GetProperty("status").GetString());

        // Assert OrderEvent(Fired) exists
        Assert.True(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "Fired"));

        // --- Start Preparing ---
        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();
        var prepBody = await prepResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Preparing", prepBody.GetProperty("status").GetString());
        Assert.Equal(orderId, prepBody.GetProperty("orderId").GetGuid());
        Assert.Equal(lineId, prepBody.GetProperty("lineId").GetGuid());

        // Assert Preparing via GET
        var get2 = await client.GetAsync($"/orders/{orderId}");
        get2.EnsureSuccessStatusCode();
        var get2Body = await get2.Content.ReadFromJsonAsync<JsonElement>();
        var line2 = get2Body.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Preparing", line2.GetProperty("status").GetString());

        // Assert OrderEvent(StartedPreparing) exists
        Assert.True(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "StartedPreparing"));

        // --- Mark Ready ---
        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();
        var readyBody = await readyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", readyBody.GetProperty("status").GetString());

        // Assert Ready via GET
        var get3 = await client.GetAsync($"/orders/{orderId}");
        get3.EnsureSuccessStatusCode();
        var get3Body = await get3.Content.ReadFromJsonAsync<JsonElement>();
        var line3 = get3Body.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Ready", line3.GetProperty("status").GetString());

        // Assert OrderEvent(MarkedReady) exists
        Assert.True(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "MarkedReady"));

        // --- Serve ---
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();
        var serveBody = await serveResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Served", serveBody.GetProperty("status").GetString());

        // Assert Served via GET
        var get4 = await client.GetAsync($"/orders/{orderId}");
        get4.EnsureSuccessStatusCode();
        var get4Body = await get4.Content.ReadFromJsonAsync<JsonElement>();
        var line4 = get4Body.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Served", line4.GetProperty("status").GetString());

        // Assert OrderEvent(Served) exists
        Assert.True(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "Served"));

        // Assert KDS tickets no longer include the line (Ready/Served excluded)
        var ticketsResponse = await client.GetAsync($"/kds/stations/{stationId}/tickets");
        ticketsResponse.EnsureSuccessStatusCode();
        var ticketsBody = await ticketsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tickets = ticketsBody.EnumerateArray().ToList();
        Assert.Empty(tickets);
    }

    [Fact]
    public async Task Invalid_transition_returns_409()
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
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
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

        // Fire
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        // Calling /ready on a Fired (not Preparing) line must 409
        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        Assert.Equal(409, (int)readyResponse.StatusCode);

        var errorBody = await readyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Line is not Preparing", errorBody.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Waiter_receives_line_status_changed()
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
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
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

        // Fire
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        var server = factory.Server;

        var statusChanged = new TaskCompletionSource<JsonElement>();

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/posHub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token)!;
            })
            .Build();

        connection.On<JsonElement>("LineStatusChanged", data =>
        {
            statusChanged.TrySetResult(data);
        });

        await connection.StartAsync();
        await connection.SendAsync("JoinWaiterGroup");
        await Task.Delay(200);

        // Call start-preparing
        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();

        var result = await statusChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(orderId, result.GetProperty("orderId").GetGuid());
        Assert.Equal(lineId, result.GetProperty("lineId").GetGuid());
        Assert.Equal("Preparing", result.GetProperty("status").GetString());

        await connection.StopAsync();
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

    private static async Task<bool> OrderEventExistsAsync(string connectionString, Guid tenantId, Guid orderId, string eventType)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM order_events WHERE order_id = @orderId AND event_type = @eventType LIMIT 1", connection);
        cmd.Parameters.AddWithValue("orderId", orderId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }
}
