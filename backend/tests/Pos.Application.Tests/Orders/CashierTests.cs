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
public sealed class CashierTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItem1Name = "Burger";
    private const decimal MenuItem1Price = 10.00m;
    private const string MenuItem2Name = "Fries";
    private const decimal MenuItem2Price = 8.00m;
    private const string ModifierName = "Extra cheese";
    private const decimal ModifierPriceDelta = 2.00m;

    private const decimal TaxRate = 0.05m;
    private const bool TaxApplies = true;
    private const decimal ServiceChargeRate = 0.05m;
    private const bool ServiceApplies = true;
    private const bool TaxOnService = false;

    [Fact]
    public async Task Open_orders_returns_correct_statuses_and_counts()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        // Seed a second table for the completed order
        var table2Id = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fixture.AppConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(conn, tenantId);
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')",
                conn);
            cmd.Parameters.AddWithValue("id", table2Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", "14");
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Create order 1 (Open, dine-in)
        var orderResponse1 = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse1.EnsureSuccessStatusCode();
        var order1Body = await orderResponse1.Content.ReadFromJsonAsync<JsonElement>();
        var order1Id = order1Body.GetProperty("orderId").GetGuid();
        var rowVersion1 = order1Body.GetProperty("rowVersion").GetInt32();

        // Add a line and fire it (order becomes InProgress)
        var lineBody = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion = rowVersion1 };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{order1Id}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion1 = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{order1Id}/fire", new { rowVersion = rowVersion1 });
        fireResponse.EnsureSuccessStatusCode();

        // Create order 2 (Open, takeaway with customer name)
        var order2Body = new { customerName = "John Doe" };
        var orderResponse2 = await client.PostAsJsonAsync("/orders", order2Body);
        orderResponse2.EnsureSuccessStatusCode();
        var order2Data = await orderResponse2.Content.ReadFromJsonAsync<JsonElement>();
        var order2Id = order2Data.GetProperty("orderId").GetGuid();

        // Create order 3 on table2, close it (Completed)
        var orderResponse3 = await client.PostAsync($"/tables/{table2Id}/orders", null);
        orderResponse3.EnsureSuccessStatusCode();
        var order3Body = await orderResponse3.Content.ReadFromJsonAsync<JsonElement>();
        var order3Id = order3Body.GetProperty("orderId").GetGuid();
        var rowVersion3 = order3Body.GetProperty("rowVersion").GetInt32();

        var line3Body = new { menuItemId = menuItem1Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion = rowVersion3 };
        var line3Response = await client.PostAsJsonAsync($"/orders/{order3Id}/lines", line3Body);
        line3Response.EnsureSuccessStatusCode();
        var line3Resp = await line3Response.Content.ReadFromJsonAsync<JsonElement>();
        var line3Id = line3Resp.GetProperty("lineId").GetGuid();
        rowVersion3 = line3Resp.GetProperty("rowVersion").GetInt32();

        var fire3Response = await client.PostAsJsonAsync($"/orders/{order3Id}/fire", new { rowVersion = rowVersion3 });
        fire3Response.EnsureSuccessStatusCode();
        var fire3Body = await fire3Response.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion3 = fire3Body.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, order3Id, line3Id);

        var closeResponse = await client.PostAsJsonAsync($"/orders/{order3Id}/close", new { rowVersion = rowVersion3 });
        closeResponse.EnsureSuccessStatusCode();

        // GET /orders/open — should return order 1 (InProgress) and order 2 (Open), NOT order 3 (Completed)
        var openResponse = await client.GetAsync("/orders/open");
        openResponse.EnsureSuccessStatusCode();
        var openBody = await openResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orders = openBody.EnumerateArray().ToList();

        Assert.Equal(2, orders.Count);

        var order1Result = orders.Single(o => o.GetProperty("id").GetGuid() == order1Id);
        Assert.Equal("InProgress", order1Result.GetProperty("status").GetString());
        Assert.Equal(tableId, order1Result.GetProperty("tableId").GetGuid());
        Assert.Equal("12", order1Result.GetProperty("tableLabel").GetString());
        Assert.Equal("DineIn", order1Result.GetProperty("orderType").GetString());
        Assert.Equal(1, order1Result.GetProperty("lineCount").GetInt32());
        Assert.Equal(1, order1Result.GetProperty("unfinishedCount").GetInt32());

        var order2Result = orders.Single(o => o.GetProperty("id").GetGuid() == order2Id);
        Assert.Equal("Open", order2Result.GetProperty("status").GetString());
        Assert.True(order2Result.GetProperty("tableId").ValueKind == JsonValueKind.Null);
        Assert.Null(order2Result.GetProperty("tableLabel").GetString());
        Assert.Equal("Takeaway", order2Result.GetProperty("orderType").GetString());
        Assert.Equal("John Doe", order2Result.GetProperty("customerName").GetString());
        Assert.Equal(0, order2Result.GetProperty("lineCount").GetInt32());
        Assert.Equal(0, order2Result.GetProperty("unfinishedCount").GetInt32());
    }

    [Fact]
    public async Task Open_orders_rls_isolates_tenants()
    {
        var (tenantAId, branchAId, sectionAId, tableAId, waiterAId, stationAId, menuItemA1Id, _) =
            await SeedFullAsync(null, null);
        var (tenantBId, branchBId, sectionBId, tableBId, waiterBId, stationBId, menuItemB1Id, _) =
            await SeedFullAsync(null, null);

        await using var factory = CreateFactory();
        using var clientA = factory.CreateClient();
        var tokenA = await GetDevTokenAsync(clientA, tenantAId, branchAId, waiterAId);
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var clientB = factory.CreateClient();
        var tokenB = await GetDevTokenAsync(clientB, tenantBId, branchBId, waiterBId);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        // Create an open order for tenant A
        var orderAResponse = await clientA.PostAsync($"/tables/{tableAId}/orders", null);
        orderAResponse.EnsureSuccessStatusCode();
        var orderABody = await orderAResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderAId = orderABody.GetProperty("orderId").GetGuid();

        // Create an open order for tenant B
        var orderBResponse = await clientB.PostAsync($"/tables/{tableBId}/orders", null);
        orderBResponse.EnsureSuccessStatusCode();
        var orderBBody = await orderBResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderBId = orderBBody.GetProperty("orderId").GetGuid();

        // Tenant A should see only their own order
        var openAResponse = await clientA.GetAsync("/orders/open");
        openAResponse.EnsureSuccessStatusCode();
        var openABody = await openAResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ordersA = openABody.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(orderAId, ordersA);
        Assert.DoesNotContain(orderBId, ordersA);

        // Tenant B should see only their own order
        var openBResponse = await clientB.GetAsync("/orders/open");
        openBResponse.EnsureSuccessStatusCode();
        var openBBody = await openBResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ordersB = openBBody.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(orderBId, ordersB);
        Assert.DoesNotContain(orderAId, ordersB);
    }

    [Fact]
    public async Task Bill_preview_returns_canClose_false_when_lines_unfinished()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add two lines
        var modifierId = await GetModifierIdAsync(fixture.AppConnectionString, tenantId);
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        var line1Id = line1Resp.GetProperty("lineId").GetGuid();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = new[] { modifierId }, rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        var line2Id = line2Resp.GetProperty("lineId").GetGuid();
        rowVersion = line2Resp.GetProperty("rowVersion").GetInt32();

        // Fire both lines (they become Fired)
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        // Bill preview — must show canClose=false and unfinishedCount=2
        var previewResponse = await client.GetAsync($"/orders/{orderId}/bill-preview");
        previewResponse.EnsureSuccessStatusCode();
        var previewBody = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(orderId, previewBody.GetProperty("orderId").GetGuid());
        Assert.False(previewBody.GetProperty("canClose").GetBoolean());
        Assert.Equal(2, previewBody.GetProperty("unfinishedCount").GetInt32());

        // Serve one line
        await ServeLineAsync(client, orderId, line1Id);

        // Bill preview — must still show canClose=false, unfinishedCount=1
        var preview2Response = await client.GetAsync($"/orders/{orderId}/bill-preview");
        preview2Response.EnsureSuccessStatusCode();
        var preview2Body = await preview2Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview2Body.GetProperty("canClose").GetBoolean());
        Assert.Equal(1, preview2Body.GetProperty("unfinishedCount").GetInt32());

        // Serve the second line
        await ServeLineAsync(client, orderId, line2Id);

        // Bill preview — now canClose=true, unfinishedCount=0
        var preview3Response = await client.GetAsync($"/orders/{orderId}/bill-preview");
        preview3Response.EnsureSuccessStatusCode();
        var preview3Body = await preview3Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview3Body.GetProperty("canClose").GetBoolean());
        Assert.Equal(0, preview3Body.GetProperty("unfinishedCount").GetInt32());
    }

    [Fact]
    public async Task Bill_preview_and_open_orders_show_ready_line_as_unfinished()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Fire -> start-preparing -> ready (line is Ready)
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();

        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();

        // Bill-preview with Ready line — canClose=false, unfinishedCount=1
        var previewResponse = await client.GetAsync($"/orders/{orderId}/bill-preview");
        previewResponse.EnsureSuccessStatusCode();
        var previewBody = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(previewBody.GetProperty("canClose").GetBoolean());
        Assert.Equal(1, previewBody.GetProperty("unfinishedCount").GetInt32());

        // Open orders — same order has unfinishedCount=1
        var openResponse = await client.GetAsync("/orders/open");
        openResponse.EnsureSuccessStatusCode();
        var openBody = await openResponse.Content.ReadFromJsonAsync<JsonElement>();
        var openOrder = openBody.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == orderId);
        Assert.Equal(1, openOrder.GetProperty("unfinishedCount").GetInt32());

        // Serve the line — now canClose=true, unfinishedCount=0
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();

        var preview2Response = await client.GetAsync($"/orders/{orderId}/bill-preview");
        preview2Response.EnsureSuccessStatusCode();
        var preview2Body = await preview2Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview2Body.GetProperty("canClose").GetBoolean());
        Assert.Equal(0, preview2Body.GetProperty("unfinishedCount").GetInt32());
    }

    [Fact]
    public async Task Bill_preview_totals_match_close_totals()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var modifierId = await GetModifierIdAsync(fixture.AppConnectionString, tenantId);
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        var line1Id = line1Resp.GetProperty("lineId").GetGuid();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = new[] { modifierId }, rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp2 = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        var line2Id = line2Resp2.GetProperty("lineId").GetGuid();
        rowVersion = line2Resp2.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        // Serve both lines
        await ServeLineAsync(client, orderId, line1Id);
        await ServeLineAsync(client, orderId, line2Id);

        // Get bill preview
        var previewResponse = await client.GetAsync($"/orders/{orderId}/bill-preview");
        previewResponse.EnsureSuccessStatusCode();
        var previewBody = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(previewBody.GetProperty("canClose").GetBoolean());
        Assert.Equal(0, previewBody.GetProperty("unfinishedCount").GetInt32());

        var previewSubtotal = previewBody.GetProperty("subtotal").GetDecimal();
        var previewService = previewBody.GetProperty("serviceAmount").GetDecimal();
        var previewTax = previewBody.GetProperty("taxAmount").GetDecimal();
        var previewDiscount = previewBody.GetProperty("discountAmount").GetDecimal();
        var previewTotal = previewBody.GetProperty("total").GetDecimal();

        // Line 1 (Burger): 2 x 10.00 = 20.00
        // Line 2 (Fries + Extra cheese): 1 x 8.00 + 2.00 = 10.00
        // Subtotal = 30.00
        // Service = 30.00 * 0.05 = 1.50
        // Taxable base = 30.00 (tax_on_service = false)
        // Tax = 30.00 * 0.05 = 1.50
        // Total = 30.00 + 1.50 + 1.50 = 33.00
        Assert.Equal(30.00m, previewSubtotal);
        Assert.Equal(1.50m, previewService);
        Assert.Equal(1.50m, previewTax);
        Assert.Equal(0m, previewDiscount);
        Assert.Equal(33.00m, previewTotal);

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();
        var closeBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Assert preview totals match close totals exactly
        Assert.Equal(previewSubtotal, closeBody.GetProperty("subtotal").GetDecimal());
        Assert.Equal(previewService, closeBody.GetProperty("serviceAmount").GetDecimal());
        Assert.Equal(previewTax, closeBody.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(previewDiscount, closeBody.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(previewTotal, closeBody.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Bill_preview_does_not_mutate()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();

        await ServeLineAsync(client, orderId, lineId);

        // Record order state before preview
        var getBeforeResponse = await client.GetAsync($"/orders/{orderId}");
        getBeforeResponse.EnsureSuccessStatusCode();
        var getBeforeBody = await getBeforeResponse.Content.ReadFromJsonAsync<JsonElement>();

        var beforeStatus = getBeforeBody.GetProperty("status").GetString();
        var beforeRowVersion = getBeforeBody.GetProperty("rowVersion").GetInt32();
        // Assert snapshot columns are still null (not yet closed)
        Assert.Equal(JsonValueKind.Null, getBeforeBody.GetProperty("subtotalAmount").ValueKind);
        Assert.Equal("InProgress", beforeStatus);

        // Call bill-preview (non-mutating)
        var previewResponse = await client.GetAsync($"/orders/{orderId}/bill-preview");
        previewResponse.EnsureSuccessStatusCode();

        // Assert state is unchanged
        var getAfterResponse = await client.GetAsync($"/orders/{orderId}");
        getAfterResponse.EnsureSuccessStatusCode();
        var getAfterBody = await getAfterResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("InProgress", getAfterBody.GetProperty("status").GetString());
        Assert.Equal(beforeRowVersion, getAfterBody.GetProperty("rowVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, getAfterBody.GetProperty("subtotalAmount").ValueKind);

        // Assert no Completed OrderEvent was created
        Assert.False(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "Completed"));
    }

    [Fact]
    public async Task Bill_preview_returns_409_for_completed_order()
    {
        var (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) = await SeedFullAsync(
            null, null);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, orderId, lineId);

        // Close the order
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();

        // Bill preview on completed order must return 409
        var previewResponse = await client.GetAsync($"/orders/{orderId}/bill-preview");
        Assert.Equal(409, (int)previewResponse.StatusCode);
        var errorBody = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order already closed", errorBody.GetProperty("title").GetString());
    }

    // --- Test infrastructure (mirrors CloseAndReceiptTests) ---

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
            role = "Cashier",
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

    private async Task<(Guid tenantId, Guid branchId, Guid sectionId, Guid tableId, Guid waiterId, Guid stationId, Guid menuItem1Id, Guid menuItem2Id)> SeedFullAsync(
        string? printerIp, int? printerPort)
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItem1Id = Guid.NewGuid();
        var menuItem2Id = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var modifierGroupId = Guid.NewGuid();

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
            "INSERT INTO branch_settings (id, tenant_id, branch_id, currency, tax_rate, tax_applies, service_charge_rate, service_applies, tax_on_service, printer_ip, printer_port) VALUES (@id, @tenantId, @branchId, @currency, @taxRate, @taxApplies, @serviceChargeRate, @serviceApplies, @taxOnService, @printerIp, @printerPort)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("currency", "USD");
            cmd.Parameters.AddWithValue("taxRate", TaxRate);
            cmd.Parameters.AddWithValue("taxApplies", TaxApplies);
            cmd.Parameters.AddWithValue("serviceChargeRate", ServiceChargeRate);
            cmd.Parameters.AddWithValue("serviceApplies", ServiceApplies);
            cmd.Parameters.AddWithValue("taxOnService", TaxOnService);
            cmd.Parameters.AddWithValue("printerIp", (object?)printerIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("printerPort", (object?)printerPort ?? DBNull.Value);
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
            cmd.Parameters.AddWithValue("id", menuItem1Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("categoryId", categoryId);
            cmd.Parameters.AddWithValue("stationId", stationId);
            cmd.Parameters.AddWithValue("name", MenuItem1Name);
            cmd.Parameters.AddWithValue("price", MenuItem1Price);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, @name, @price, 'Available')",
            connection))
        {
            cmd.Parameters.AddWithValue("id", menuItem2Id);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("categoryId", categoryId);
            cmd.Parameters.AddWithValue("stationId", stationId);
            cmd.Parameters.AddWithValue("name", MenuItem2Name);
            cmd.Parameters.AddWithValue("price", MenuItem2Price);
            await cmd.ExecuteNonQueryAsync();
        }

        // Create modifier group and modifier linked to menuItem2
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO modifier_groups (id, tenant_id, name, min_select, max_select, required) VALUES (@id, @tenantId, @name, 0, 1, false)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", modifierGroupId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Extras");
            await cmd.ExecuteNonQueryAsync();
        }

        var modifierId = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO modifiers (id, tenant_id, group_id, name, price_delta) VALUES (@id, @tenantId, @groupId, @name, @priceDelta)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", modifierId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("groupId", modifierGroupId);
            cmd.Parameters.AddWithValue("name", ModifierName);
            cmd.Parameters.AddWithValue("priceDelta", ModifierPriceDelta);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO item_modifier_groups (id, tenant_id, item_id, group_id) VALUES (@id, @tenantId, @itemId, @groupId)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("itemId", menuItem2Id);
            cmd.Parameters.AddWithValue("groupId", modifierGroupId);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        return (tenantId, branchId, sectionId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id);
    }

    private static async Task<Guid> GetModifierIdAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM modifiers WHERE tenant_id = @tenantId LIMIT 1", connection);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
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
