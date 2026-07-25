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
public sealed class OrderConcurrencyTests(PostgresRlsFixture fixture)
{
    private const string SectionName = "Terrace";
    private const string TableLabel = "12";
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;
    private const string ModifierName = "Extra Cheese";
    private const decimal ModifierPriceDelta = 2.00m;

    [Fact]
    public async Task Cannot_open_two_orders_on_one_table()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedMinimalAsync(tenantId, branchId, waiterId, sectionId, tableId);

        var firstResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        firstResponse.EnsureSuccessStatusCode();

        var secondResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        Assert.Equal(409, (int)secondResponse.StatusCode);
    }

    [Fact]
    public async Task Stale_row_version_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var modifierGroupId = Guid.NewGuid();
        var modifierId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId, modifierGroupId, modifierId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();
        Assert.Equal(1, rowVersion);

        var addLineBody = new { menuItemId, quantity = 2, modifierIds = new[] { modifierId }, rowVersion };
        var addResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", addLineBody);
        addResponse.EnsureSuccessStatusCode();
        var addBody = await addResponse.Content.ReadFromJsonAsync<JsonElement>();
        _ = addBody.GetProperty("rowVersion").GetInt32();

        var staleBody = new { menuItemId, quantity = 1, modifierIds = Array.Empty<Guid>(), rowVersion };
        var staleResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", staleBody);
        Assert.Equal(409, (int)staleResponse.StatusCode);

        var staleError = await staleResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Concurrency conflict", staleError.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Draft_line_snapshots_price_and_modifiers()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var modifierGroupId = Guid.NewGuid();
        var modifierId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, menuItemId, modifierGroupId, modifierId);

        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();
        var orderBody = await orderResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = orderBody.GetProperty("orderId").GetGuid();
        var rowVersion = orderBody.GetProperty("rowVersion").GetInt32();

        var addLineBody = new { menuItemId, quantity = 2, modifierIds = new[] { modifierId }, rowVersion };
        var addResponse = await client.PostAsJsonAsync($"/orders/{orderId}/lines", addLineBody);
        addResponse.EnsureSuccessStatusCode();
        var addBody = await addResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MenuItemPrice, addBody.GetProperty("unitPrice").GetDecimal());

        var getResponse = await client.GetAsync($"/orders/{orderId}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        var lines = getBody.GetProperty("lines").EnumerateArray().ToList();

        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(MenuItemPrice, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("Draft", line.GetProperty("status").GetString());

        var mods = line.GetProperty("modifiers").EnumerateArray().ToList();
        Assert.Single(mods);
        Assert.Equal(ModifierName, mods[0].GetProperty("name").GetString());
        Assert.Equal(ModifierPriceDelta, mods[0].GetProperty("priceDelta").GetDecimal());
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
                    ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
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

    private async Task SeedMinimalAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId)
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
            "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("role", "Waiter");
            cmd.Parameters.AddWithValue("permission", "TakeOrder");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", sectionId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", SectionName);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", tableId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", TableLabel);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task SeedFullAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId, Guid stationId,
        Guid menuItemId, Guid modifierGroupId, Guid modifierId)
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
            "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)", connection))
        {
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("role", "Waiter");
            cmd.Parameters.AddWithValue("permission", "TakeOrder");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", sectionId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", SectionName);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", tableId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", TableLabel);
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
            cmd.Parameters.AddWithValue("name", "Main Course");
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

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO modifier_groups (id, tenant_id, name, min_select, max_select, required) VALUES (@id, @tenantId, @name, 0, 1, false)", connection))
        {
            cmd.Parameters.AddWithValue("id", modifierGroupId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Extras");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO modifiers (id, tenant_id, group_id, name, price_delta) VALUES (@id, @tenantId, @groupId, @name, @priceDelta)", connection))
        {
            cmd.Parameters.AddWithValue("id", modifierId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("groupId", modifierGroupId);
            cmd.Parameters.AddWithValue("name", ModifierName);
            cmd.Parameters.AddWithValue("priceDelta", ModifierPriceDelta);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
