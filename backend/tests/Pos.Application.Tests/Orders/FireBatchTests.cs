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
public sealed class FireBatchTests(PostgresRlsFixture fixture)
{
    private const string Station1Name = "Grill";
    private const string Station2Name = "Bar";
    private const string MenuItem1Name = "Burger";
    private const decimal MenuItem1Price = 15.50m;
    private const string MenuItem2Name = "Cocktail";
    private const decimal MenuItem2Price = 12.00m;

    [Fact]
    public async Task Fire_sends_each_stations_lines_only_to_its_group()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var station1Id = Guid.NewGuid();
        var station2Id = Guid.NewGuid();
        var menuItem1Id = Guid.NewGuid();
        var menuItem2Id = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedStationsAndItemsAsync(tenantId, branchId, waiterId, sectionId, tableId, station1Id, station2Id, menuItem1Id, menuItem2Id);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add a line for station 1
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1BodyResp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line1BodyResp.GetProperty("rowVersion").GetInt32();

        // Add a line for station 2
        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2BodyResp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line2BodyResp.GetProperty("rowVersion").GetInt32();

        var server = factory.Server;

        var station1Received = new TaskCompletionSource<JsonElement>();
        var station2Received = new TaskCompletionSource<JsonElement>();

        var conn1 = new HubConnectionBuilder()
            .WithUrl("http://localhost/posHub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token)!;
            })
            .Build();

        conn1.On<JsonElement>("OrderFired", data =>
        {
            station1Received.TrySetResult(data);
        });

        var conn2 = new HubConnectionBuilder()
            .WithUrl("http://localhost/posHub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token)!;
            })
            .Build();

        conn2.On<JsonElement>("OrderFired", data =>
        {
            station2Received.TrySetResult(data);
        });

        await conn1.StartAsync();
        await conn2.StartAsync();

        await conn1.SendAsync("JoinStationGroup", station1Id);
        await conn2.SendAsync("JoinStationGroup", station2Id);

        // Small delay to let group joins propagate
        await Task.Delay(200);

        // Fire
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        var station1Result = await station1Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var station2Result = await station2Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var station1Lines = station1Result.GetProperty("lines").EnumerateArray().ToList();
        var station2Lines = station2Result.GetProperty("lines").EnumerateArray().ToList();

        Assert.Single(station1Lines);
        Assert.Equal(menuItem1Id, station1Lines[0].GetProperty("itemId").GetGuid());

        Assert.Single(station2Lines);
        Assert.Equal(menuItem2Id, station2Lines[0].GetProperty("itemId").GetGuid());

        await conn1.StopAsync();
        await conn2.StopAsync();
    }

    [Fact]
    public async Task Second_fire_creates_new_batch_does_not_touch_first()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var station1Id = Guid.NewGuid();
        var station2Id = Guid.NewGuid();
        var menuItem1Id = Guid.NewGuid();
        var menuItem2Id = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedStationsAndItemsAsync(tenantId, branchId, waiterId, sectionId, tableId, station1Id, station2Id, menuItem1Id, menuItem2Id);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add first line
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        // Fire first batch
        var fire1Response = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fire1Response.EnsureSuccessStatusCode();
        var fire1Body = await fire1Response.Content.ReadFromJsonAsync<JsonElement>();
        var firstFireBatchId = fire1Body.GetProperty("fireBatchId").GetGuid();
        rowVersion = fire1Body.GetProperty("rowVersion").GetInt32();
        Assert.Equal(1, fire1Body.GetProperty("firedLines").GetInt32());

        // Add a second line (for the other station)
        var line2Body = new { menuItemId = menuItem2Id, quantity = 3, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line2Resp.GetProperty("rowVersion").GetInt32();

        // Fire second batch
        var fire2Response = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fire2Response.EnsureSuccessStatusCode();
        var fire2Body = await fire2Response.Content.ReadFromJsonAsync<JsonElement>();
        var secondFireBatchId = fire2Body.GetProperty("fireBatchId").GetGuid();
        rowVersion = fire2Body.GetProperty("rowVersion").GetInt32();
        Assert.Equal(1, fire2Body.GetProperty("firedLines").GetInt32());

        // Fire batch IDs must be different
        Assert.NotEqual(firstFireBatchId, secondFireBatchId);

        // Get order to verify lines have correct fire batch IDs and status
        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lines = getBody.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);

        var line1 = lines[0];
        Assert.Equal("Fired", line1.GetProperty("status").GetString());
        Assert.Equal(firstFireBatchId, line1.GetProperty("fireBatchId").GetGuid());

        var line2 = lines[1];
        Assert.Equal("Fired", line2.GetProperty("status").GetString());
        Assert.Equal(secondFireBatchId, line2.GetProperty("fireBatchId").GetGuid());
    }

    [Fact]
    public async Task Kds_tickets_returns_correct_grouping()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var station1Id = Guid.NewGuid();
        var station2Id = Guid.NewGuid();
        var menuItem1Id = Guid.NewGuid();
        var menuItem2Id = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedStationsAndItemsAsync(tenantId, branchId, waiterId, sectionId, tableId, station1Id, station2Id, menuItem1Id, menuItem2Id);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add line for station 1
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        // Add line for station 2
        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = line2Resp.GetProperty("rowVersion").GetInt32();

        // Fire all lines
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, fireBody.GetProperty("firedLines").GetInt32());

        // Get KDS tickets for station 1
        var tickets1Response = await client.GetAsync($"/kds/stations/{station1Id}/tickets");
        tickets1Response.EnsureSuccessStatusCode();
        var tickets1Body = await tickets1Response.Content.ReadFromJsonAsync<JsonElement>();
        var tickets1 = tickets1Body.EnumerateArray().ToList();

        Assert.Single(tickets1); // one ticket for station 1
        var ticket1 = tickets1[0];
        Assert.Equal(orderId, ticket1.GetProperty("orderId").GetGuid());
        var ticket1Lines = ticket1.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(ticket1Lines);
        Assert.Equal(menuItem1Id, ticket1Lines[0].GetProperty("itemId").GetGuid());

        // Get KDS tickets for station 2
        var tickets2Response = await client.GetAsync($"/kds/stations/{station2Id}/tickets");
        tickets2Response.EnsureSuccessStatusCode();
        var tickets2Body = await tickets2Response.Content.ReadFromJsonAsync<JsonElement>();
        var tickets2 = tickets2Body.EnumerateArray().ToList();

        Assert.Single(tickets2);
        var ticket2 = tickets2[0];
        Assert.Equal(orderId, ticket2.GetProperty("orderId").GetGuid());
        var ticket2Lines = ticket2.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(ticket2Lines);
        Assert.Equal(menuItem2Id, ticket2Lines[0].GetProperty("itemId").GetGuid());
    }

    [Fact]
    public async Task Fire_with_no_draft_lines_returns_409()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var station1Id = Guid.NewGuid();
        var station2Id = Guid.NewGuid();
        var menuItem1Id = Guid.NewGuid();
        var menuItem2Id = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedStationsAndItemsAsync(tenantId, branchId, waiterId, sectionId, tableId, station1Id, station2Id, menuItem1Id, menuItem2Id);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Fire with no lines
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        Assert.Equal(409, (int)fireResponse.StatusCode);
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
                    // Unlike BranchesEndpointRlsTests/OrderConcurrencyTests (which poison this
                    // to make the Redis health check fail fast, since they never touch Redis),
                    // these tests exercise the SignalR hub for real — the backplane needs an
                    // actually-reachable Redis, so this points at the same dev container the
                    // rest of the app uses.
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

    private async Task SeedStationsAndItemsAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId,
        Guid station1Id, Guid station2Id,
        Guid menuItem1Id, Guid menuItem2Id)
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
            cmd.Parameters.AddWithValue("id", station1Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", Station1Name);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", station2Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", Station2Name);
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
            cmd.Parameters.AddWithValue("id", menuItem1Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("categoryId", categoryId);
            cmd.Parameters.AddWithValue("stationId", station1Id);
            cmd.Parameters.AddWithValue("name", MenuItem1Name);
            cmd.Parameters.AddWithValue("price", MenuItem1Price);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, @name, @price, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", menuItem2Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("categoryId", categoryId);
            cmd.Parameters.AddWithValue("stationId", station2Id);
            cmd.Parameters.AddWithValue("name", MenuItem2Name);
            cmd.Parameters.AddWithValue("price", MenuItem2Price);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
