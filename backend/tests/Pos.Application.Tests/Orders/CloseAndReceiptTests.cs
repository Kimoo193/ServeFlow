using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests.Orders;

[Collection(RlsCollection.Name)]
public sealed class CloseAndReceiptTests(PostgresRlsFixture fixture)
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
    public async Task Full_close_flow_computes_correct_totals_and_releases_table()
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

        // Add line 1 (no modifiers)
        var line1Body = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        var line1Id = line1Resp.GetProperty("lineId").GetGuid();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        // Add line 2 (with modifier)
        var modifierId = await GetModifierIdAsync(fixture.AppConnectionString, tenantId);
        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = new[] { modifierId }, rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        var line2Id = line2Resp.GetProperty("lineId").GetGuid();
        rowVersion = line2Resp.GetProperty("rowVersion").GetInt32();

        // Fire
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        // Serve both lines: start-preparing -> ready -> serve
        await ServeLineAsync(client, orderId, line1Id);
        await ServeLineAsync(client, orderId, line2Id);

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();
        var closeBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(orderId, closeBody.GetProperty("orderId").GetGuid());
        Assert.Equal("Completed", closeBody.GetProperty("status").GetString());
        Assert.Equal(30.00m, closeBody.GetProperty("subtotal").GetDecimal());
        Assert.Equal(1.50m, closeBody.GetProperty("serviceAmount").GetDecimal());
        Assert.Equal(1.50m, closeBody.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(0m, closeBody.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(33.00m, closeBody.GetProperty("total").GetDecimal());
        var newRowVersion = closeBody.GetProperty("rowVersion").GetInt32();
        Assert.Equal(rowVersion + 1, newRowVersion);

        // Assert GET /orders/{orderId} shows snapshot values
        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", getBody.GetProperty("status").GetString());
        Assert.Equal(30.00m, getBody.GetProperty("subtotalAmount").GetDecimal());
        Assert.Equal(1.50m, getBody.GetProperty("serviceAmount").GetDecimal());
        Assert.Equal(1.50m, getBody.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(0m, getBody.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(33.00m, getBody.GetProperty("totalAmount").GetDecimal());

        // Assert table status is back to Available
        var tablesResponse = await client.GetAsync("/tables");
        tablesResponse.EnsureSuccessStatusCode();
        var tablesBody = await tablesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tableEntry = tablesBody.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == tableId);
        Assert.Equal("Available", tableEntry.GetProperty("status").GetString());

        // Assert OrderEvent(Completed) exists with correct details
        Assert.True(await OrderEventExistsAsync(fixture.AppConnectionString, tenantId, orderId, "Completed"));
    }

    [Fact]
    public async Task Close_blocked_when_line_is_fired()
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
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Fire (line is now Fired)
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        // Attempt close while line is Fired — must fail
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        Assert.Equal(409, (int)closeResponse.StatusCode);
        var errorBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order has unfinished lines", errorBody.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Close_blocked_when_line_is_draft()
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

        // Add a draft line but do NOT fire it
        var lineBody = new { menuItemId = menuItem1Id, quantity = 2, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Attempt close while line is Draft — must fail with 409
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        Assert.Equal(409, (int)closeResponse.StatusCode);
        var errorBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order has unfinished lines", errorBody.GetProperty("title").GetString());

        // Delete (void) the draft line and close — draft was never billed
        var deleteResponse = await client.DeleteAsync($"/orders/{orderId}/lines/{lineResp.GetProperty("lineId").GetGuid()}?rowVersion={rowVersion}");
        deleteResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var closeOkResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeOkResponse.EnsureSuccessStatusCode();
        var closeBody = await closeOkResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Bill is 0 — the Draft line was never billed
        Assert.Equal(0m, closeBody.GetProperty("subtotal").GetDecimal());
        Assert.Equal(0m, closeBody.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Close_blocked_when_line_is_ready()
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

        // Fire -> start-preparing -> ready (line is now Ready)
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        var prepResponse = await client.PostAsync($"/kds/lines/{lineId}/start-preparing", null);
        prepResponse.EnsureSuccessStatusCode();

        var readyResponse = await client.PostAsync($"/kds/lines/{lineId}/ready", null);
        readyResponse.EnsureSuccessStatusCode();

        // Attempt close while line is Ready — must fail with 409
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        Assert.Equal(409, (int)closeResponse.StatusCode);
        var errorBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order has unfinished lines", errorBody.GetProperty("title").GetString());

        // Serve the line, then close succeeds
        var serveResponse = await client.PostAsync($"/orders/{orderId}/lines/{lineId}/serve", null);
        serveResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = getBody.GetProperty("rowVersion").GetInt32();

        var closeOkResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeOkResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Receipt_returns_409_before_close_and_content_after()
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
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, orderId, lineId);

        // Receipt before close must return 409
        var receiptBeforeResponse = await client.GetAsync($"/orders/{orderId}/receipt");
        Assert.Equal(409, (int)receiptBeforeResponse.StatusCode);
        var errorBody = await receiptBeforeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order is not closed yet", errorBody.GetProperty("title").GetString());

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();

        // Receipt after close must succeed and contain correct amounts
        var receiptAfterResponse = await client.GetAsync($"/orders/{orderId}/receipt");
        receiptAfterResponse.EnsureSuccessStatusCode();
        var receiptBody = await receiptAfterResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(orderId, receiptBody.GetProperty("orderId").GetGuid());
        Assert.Equal(20.00m, receiptBody.GetProperty("subtotal").GetDecimal());
        Assert.Equal(1.00m, receiptBody.GetProperty("serviceAmount").GetDecimal());
        Assert.Equal(1.00m, receiptBody.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(22.00m, receiptBody.GetProperty("total").GetDecimal());

        var lines = receiptBody.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        Assert.Equal(2, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(20.00m, lines[0].GetProperty("lineTotal").GetDecimal());
    }

    [Fact]
    public async Task Print_receipt_sends_escpos_bytes()
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
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, orderId, lineId);

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();

        // Set up a TCP listener to receive the print bytes
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Update branch_settings with printer IP/port
            await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RlsTestSql.SetTenantAsync(connection, tenantId);
            await using (var cmd = new NpgsqlCommand(
                "UPDATE branch_settings SET printer_ip = @ip, printer_port = @port WHERE branch_id = @branchId",
                connection))
            {
                cmd.Parameters.AddWithValue("ip", "127.0.0.1");
                cmd.Parameters.AddWithValue("port", port);
                cmd.Parameters.AddWithValue("branchId", branchId);
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();

            // Accept connection from printer emulator asynchronously
            var acceptTask = listener.AcceptTcpClientAsync();

            // Call the print endpoint
            var printResponse = await client.PostAsync($"/orders/{orderId}/receipt/print", null);
            printResponse.EnsureSuccessStatusCode();
            var printBody = await printResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(printBody.GetProperty("printed").GetBoolean());

            // Read bytes received by the fake printer
            using var printerClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            await using var stream = printerClient.GetStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var receivedBytes = ms.ToArray();

            // Assert ESC/POS init sequence at start
            Assert.Equal(0x1B, receivedBytes[0]);
            Assert.Equal(0x40, receivedBytes[1]);

            // Assert full-cut sequence at end
            var cutSequence = new byte[] { 0x1D, 0x56, 0x00 };
            var cutStart = receivedBytes.Length - cutSequence.Length;
            Assert.True(cutStart >= 0);
            for (int i = 0; i < cutSequence.Length; i++)
                Assert.Equal(cutSequence[i], receivedBytes[cutStart + i]);

            // Assert receipt text content is present
            var text = Encoding.UTF8.GetString(receivedBytes);
            Assert.Contains("2x", text);
            Assert.Contains("Subtotal:", text);
            Assert.Contains("TOTAL:", text);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Print_returns_400_when_printer_not_configured()
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

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();

        // Print without configuring printer — must fail 400
        var printResponse = await client.PostAsync($"/orders/{orderId}/receipt/print", null);
        Assert.Equal(400, (int)printResponse.StatusCode);
        var errorBody = await printResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Printer not configured for this branch", errorBody.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Void_on_completed_order_returns_409()
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
        var closeBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var closeRowVersion = closeBody.GetProperty("rowVersion").GetInt32();

        // Attempt to void the served line after close — must return 409
        var voidResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines/{lineId}/void", new { reason = "Test void after close", rowVersion = closeRowVersion });
        Assert.Equal(409, (int)voidResponse.StatusCode);
        var errorBody = await voidResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Order is already completed", errorBody.GetProperty("title").GetString());
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

    private static async Task<string> GetDevTokenAsync(HttpClient client, Guid tenantId, Guid branchId, Guid userId)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId,
            branchId,
            userId,
            role = "Owner",
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
