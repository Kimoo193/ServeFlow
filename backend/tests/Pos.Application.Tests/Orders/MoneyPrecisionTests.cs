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
public sealed class MoneyPrecisionTests(PostgresRlsFixture fixture)
{
    [Fact]
    public async Task Close_with_fractional_rates_rounds_every_component_to_2dp_and_components_sum_to_total()
    {
        const decimal taxRate = 0.14m;
        const decimal serviceChargeRate = 0.12m;
        const bool taxOnService = true;
        const decimal item1Price = 6.68m;
        const decimal item2Price = 6.69m;

        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id) =
            await SeedMoneyTestAsync(taxRate, serviceChargeRate, taxOnService, item1Price, item2Price);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Line 1: 1× 6.68 = 6.68
        var line1Body = new { menuItemId = menuItem1Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line1Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line1Body);
        line1Response.EnsureSuccessStatusCode();
        var line1Resp = await line1Response.Content.ReadFromJsonAsync<JsonElement>();
        var line1Id = line1Resp.GetProperty("lineId").GetGuid();
        rowVersion = line1Resp.GetProperty("rowVersion").GetInt32();

        // Line 2: 1× 6.69 = 6.69  → subtotal = 13.37
        var line2Body = new { menuItemId = menuItem2Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var line2Response = await client.PostAsJsonAsync($"/orders/{orderId}/lines", line2Body);
        line2Response.EnsureSuccessStatusCode();
        var line2Resp = await line2Response.Content.ReadFromJsonAsync<JsonElement>();
        var line2Id = line2Resp.GetProperty("lineId").GetGuid();
        rowVersion = line2Resp.GetProperty("rowVersion").GetInt32();

        // Fire -> start-preparing -> ready -> serve
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        await ServeLineAsync(client, orderId, line1Id);
        await ServeLineAsync(client, orderId, line2Id);

        var getAfterServe = await client.GetAsync($"/orders/{orderId}");
        getAfterServe.EnsureSuccessStatusCode();
        var afterServeBody = await getAfterServe.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = afterServeBody.GetProperty("rowVersion").GetInt32();

        // Close
        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();
        var closeBody = await closeResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Expected: subtotal=13.37, service=1.60, tax=2.10, total=17.07
        const decimal expectedSubtotal = 13.37m;
        const decimal expectedService = 1.60m;  // round(13.37 * 0.12, 2) = round(1.6044) = 1.60
        const decimal expectedTax = 2.10m;       // round((13.37+1.60)*0.14, 2) = round(14.97*0.14) = round(2.0958) = 2.10
        const decimal expectedTotal = 17.07m;

        Assert.Equal(expectedSubtotal, closeBody.GetProperty("subtotal").GetDecimal());
        Assert.Equal(expectedService, closeBody.GetProperty("serviceAmount").GetDecimal());
        Assert.Equal(expectedTax, closeBody.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(0m, closeBody.GetProperty("discountAmount").GetDecimal());
        Assert.Equal(expectedTotal, closeBody.GetProperty("total").GetDecimal());

        // Components sum to total exactly
        Assert.Equal(
            expectedSubtotal + expectedService + expectedTax - 0m,
            closeBody.GetProperty("total").GetDecimal());

        // Each component has at most 2 decimal places (verify via stored values in DB)
        var subtotalFromClose = closeBody.GetProperty("subtotal").GetDecimal();
        var serviceFromClose = closeBody.GetProperty("serviceAmount").GetDecimal();
        var taxFromClose = closeBody.GetProperty("taxAmount").GetDecimal();
        var totalFromClose = closeBody.GetProperty("total").GetDecimal();

        Assert.Equal(2, (decimal.GetBits(subtotalFromClose)[3] >> 16) & 0xFF);
        Assert.Equal(2, (decimal.GetBits(serviceFromClose)[3] >> 16) & 0xFF);
        Assert.Equal(2, (decimal.GetBits(taxFromClose)[3] >> 16) & 0xFF);
        Assert.Equal(2, (decimal.GetBits(totalFromClose)[3] >> 16) & 0xFF);

        // Verify stored values in database match the :F2 receipt format
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT subtotal_amount, service_amount, tax_amount, discount_amount, total_amount FROM orders WHERE id = @id",
            connection);
        cmd.Parameters.AddWithValue("id", orderId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var dbSubtotal = reader.GetDecimal(0);
        var dbService = reader.GetDecimal(1);
        var dbTax = reader.GetDecimal(2);
        var dbDiscount = reader.GetDecimal(3);
        var dbTotal = reader.GetDecimal(4);

        // Stored values match the close response
        Assert.Equal(expectedSubtotal, dbSubtotal);
        Assert.Equal(expectedService, dbService);
        Assert.Equal(expectedTax, dbTax);
        Assert.Equal(0m, dbDiscount);
        Assert.Equal(expectedTotal, dbTotal);

        // Stored total equals receipt :F2 rendering
        Assert.Equal(dbTotal, decimal.Parse(expectedTotal.ToString("F2")));
    }

    [Fact]
    public async Task Add_line_with_quantity_zero_returns_400()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = 0, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);

        Assert.Equal((int)HttpStatusCode.BadRequest, (int)lineResponse.StatusCode);
        var error = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid quantity", error.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Add_line_with_quantity_negative_returns_400()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = -1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);

        Assert.Equal((int)HttpStatusCode.BadRequest, (int)lineResponse.StatusCode);
        var error = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid quantity", error.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_line_to_negative_quantity_returns_400()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add a valid line first
        var lineBody = new { menuItemId = menuItem1Id, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Patch to negative
        var patchBody = new { quantity = -5, modifierIds = Array.Empty<Guid>(), rowVersion };
        var patchResponse = await client.PatchAsJsonAsync($"/orders/{orderId}/lines/{lineId}", patchBody);

        Assert.Equal((int)HttpStatusCode.BadRequest, (int)patchResponse.StatusCode);
        var error = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid quantity", error.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Increase_quantity_overflow_returns_400()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Add line at the maximum permitted quantity.
        var lineBody = new { menuItemId = menuItem1Id, quantity = 999, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
        var lineResp = await lineResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = lineResp.GetProperty("lineId").GetGuid();
        rowVersion = lineResp.GetProperty("rowVersion").GetInt32();

        // Fire it (needed for increase-quantity to work)
        var fireResponse = await client.PostAsJsonAsync($"/orders/{orderId}/fire", new { rowVersion });
        fireResponse.EnsureSuccessStatusCode();
        var fireBody = await fireResponse.Content.ReadFromJsonAsync<JsonElement>();
        rowVersion = fireBody.GetProperty("rowVersion").GetInt32();

        // A delta above the bound is refused: it would become a new line of that quantity.
        var tooLarge = await client.PostAsJsonAsync(
            $"/orders/{orderId}/lines/{lineId}/increase-quantity",
            new { quantityDelta = 1000, rowVersion });

        Assert.Equal((int)HttpStatusCode.BadRequest, (int)tooLarge.StatusCode);
        var error = await tooLarge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid quantity", error.GetProperty("title").GetString());

        // But a +1 on an already-maximal line SUCCEEDS. increase-quantity does not increment in
        // place — it adds a new line carrying the delta — so the resulting line has quantity 1
        // and is perfectly legal. Guarding line.Quantity + delta would wrongly refuse this.
        // 201 Created, not 200: a new OrderLine is what the endpoint produces, and asserting
        // Created is what pins that semantic down.
        var permitted = await client.PostAsJsonAsync(
            $"/orders/{orderId}/lines/{lineId}/increase-quantity",
            new { quantityDelta = 1, rowVersion });

        Assert.Equal((int)HttpStatusCode.Created, (int)permitted.StatusCode);

        var permittedBody = await permitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, permittedBody.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Valid_quantity_add_line_succeeds()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var lineBody = new { menuItemId = menuItem1Id, quantity = 3, modifierIds = Array.Empty<Guid>(), rowVersion };
        var lineResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", lineBody);
        lineResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Order_currency_is_snapshotted_at_close()
    {
        var (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, _) =
            await SeedMoneyTestAsync(0.05m, 0.05m, false, 10.00m, 8.00m);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        // Before close, currency should be null
        var getBeforeClose = await client.GetAsync($"/orders/{orderId}");
        getBeforeClose.EnsureSuccessStatusCode();
        var beforeCloseBody = await getBeforeClose.Content.ReadFromJsonAsync<JsonElement>();
        // Verify we can get the order (currency isn't in the GET response, but we won't check there)

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

        var closeResponse = await client.PostAsJsonAsync($"/orders/{orderId}/close", new { rowVersion });
        closeResponse.EnsureSuccessStatusCode();

        // Verify currency is stored in the database
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var cmd = new NpgsqlCommand(
            "SELECT currency FROM orders WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", orderId);
        var dbCurrency = (string?)await cmd.ExecuteScalarAsync();

        Assert.Equal("USD", dbCurrency);
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

    private async Task<(Guid tenantId, Guid branchId, Guid tableId, Guid waiterId, Guid stationId, Guid menuItem1Id, Guid menuItem2Id)> SeedMoneyTestAsync(
        decimal taxRate, decimal serviceChargeRate, bool taxOnService,
        decimal item1Price, decimal item2Price)
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
            "INSERT INTO branch_settings (id, tenant_id, branch_id, currency, tax_rate, tax_applies, service_charge_rate, service_applies, tax_on_service) VALUES (@id, @tenantId, @branchId, @currency, @taxRate, @taxApplies, @serviceChargeRate, @serviceApplies, @taxOnService)",
            connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("currency", "USD");
            cmd.Parameters.AddWithValue("taxRate", taxRate);
            cmd.Parameters.AddWithValue("taxApplies", true);
            cmd.Parameters.AddWithValue("serviceChargeRate", serviceChargeRate);
            cmd.Parameters.AddWithValue("serviceApplies", true);
            cmd.Parameters.AddWithValue("taxOnService", taxOnService);
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
            cmd.Parameters.AddWithValue("name", "Grill");
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
            cmd.Parameters.AddWithValue("name", "Item 1");
            cmd.Parameters.AddWithValue("price", item1Price);
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
            cmd.Parameters.AddWithValue("name", "Item 2");
            cmd.Parameters.AddWithValue("price", item2Price);
            await cmd.ExecuteNonQueryAsync();
        }

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
            cmd.Parameters.AddWithValue("name", "Extra cheese");
            cmd.Parameters.AddWithValue("priceDelta", 2.00m);
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

        return (tenantId, branchId, tableId, waiterId, stationId, menuItem1Id, menuItem2Id);
    }
}
