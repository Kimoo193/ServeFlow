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

/// <summary>
/// Ordering a combo (BR-O-06). The combo is deliberately priced so the bundle does not divide
/// evenly across its components, and one component has quantity 3 — the case that forced
/// per-unit expansion, since no 2-decimal unit price on a quantity-3 line can total the bundle.
/// </summary>
[Collection(RlsCollection.Name)]
public sealed class ComboOrderingTests(PostgresRlsFixture fixture)
{
    private const decimal ComboPrice = 9.99m;
    private const decimal BurgerPrice = 8.00m;
    private const decimal FriesPrice = 2.50m;
    private const int FriesQuantity = 3;
    private const string ComboName = "Lunch Deal";

    [Fact]
    public async Task Adding_a_combo_expands_to_one_line_per_component_unit()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new
        {
            comboId = world.ComboId,
            rowVersion,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(1 + FriesQuantity, lines.Count);
        Assert.Equal(ComboName, body.GetProperty("comboName").GetString());

        // Sequences continue from the order's existing lines, without gaps or repeats.
        var sequences = lines.Select(l => l.GetProperty("sequence").GetInt32()).OrderBy(s => s).ToList();
        Assert.Equal(sequences.Distinct().Count(), sequences.Count);
        Assert.Equal(sequences.First() + sequences.Count - 1, sequences.Last());
    }

    [Fact]
    public async Task Allocated_line_prices_sum_to_exactly_the_bundle_price()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new
        {
            comboId = world.ComboId,
            rowVersion,
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var total = body.GetProperty("lines").EnumerateArray()
            .Sum(l => l.GetProperty("unitPrice").GetDecimal());

        // The whole point of the slice: not "close to", exactly.
        Assert.Equal(ComboPrice, total);
    }

    [Fact]
    public async Task Each_component_routes_to_its_own_station()
    {
        // BR-O-06: components are routed independently, so a combo's lines can legitimately
        // land on different KDS screens.
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);
        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });
        response.EnsureSuccessStatusCode();

        var stations = await QueryAsync(world.TenantId,
            "SELECT station_id FROM order_lines WHERE order_id = @orderId AND combo_id IS NOT NULL",
            orderId);

        Assert.Contains(world.GrillStationId, stations);
        Assert.Contains(world.FryerStationId, stations);
    }

    [Fact]
    public async Task Bill_preview_subtotal_equals_the_bundle_price_and_labels_the_lines()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);
        await GrantAsync(world.TenantId, "Waiter", "CloseOrder");

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);
        var added = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });
        added.EnsureSuccessStatusCode();

        var preview = await client.GetAsync($"/orders/{orderId}/bill-preview");
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ComboPrice, body.GetProperty("subtotal").GetDecimal());

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.All(lines, l => Assert.Equal(ComboName, l.GetProperty("comboName").GetString()));
    }

    [Fact]
    public async Task Replaying_the_same_combo_instance_does_not_expand_it_twice()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);
        var instanceId = Guid.NewGuid();

        var first = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new
        {
            comboId = world.ComboId,
            rowVersion,
            comboInstanceId = instanceId,
        });
        first.EnsureSuccessStatusCode();

        // Replay with a deliberately stale rowVersion — the idempotency check runs first, so
        // this must succeed rather than 409, matching the add-line contract.
        var replay = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new
        {
            comboId = world.ComboId,
            rowVersion = 0,
            comboInstanceId = instanceId,
        });
        replay.EnsureSuccessStatusCode();

        var lineIds = await QueryAsync(world.TenantId,
            "SELECT id FROM order_lines WHERE order_id = @orderId", orderId);
        Assert.Equal(1 + FriesQuantity, lineIds.Count);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            firstBody.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineId").GetGuid()).OrderBy(g => g),
            replayBody.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineId").GetGuid()).OrderBy(g => g));
    }

    [Fact]
    public async Task Two_instances_of_the_same_combo_stay_distinct()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        var first = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion, comboInstanceId = Guid.NewGuid() });
        first.EnsureSuccessStatusCode();
        var nextRowVersion = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowVersion").GetInt32();

        var second = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion = nextRowVersion, comboInstanceId = Guid.NewGuid() });
        second.EnsureSuccessStatusCode();

        var instances = await QueryAsync(world.TenantId,
            "SELECT DISTINCT combo_instance_id FROM order_lines WHERE order_id = @orderId", orderId);
        Assert.Equal(2, instances.Count);

        var allLines = await QueryAsync(world.TenantId,
            "SELECT id FROM order_lines WHERE order_id = @orderId", orderId);
        Assert.Equal(2 * (1 + FriesQuantity), allLines.Count);
    }

    [Fact]
    public async Task An_out_of_stock_component_rejects_the_whole_combo()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);
        await ExecuteAsync(world.TenantId,
            "UPDATE menu_items SET availability = 'OutOfStock' WHERE id = @id", world.FriesId);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var lines = await QueryAsync(world.TenantId, "SELECT id FROM order_lines WHERE order_id = @orderId", orderId);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_combo_with_no_components_cannot_be_ordered()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);
        await ExecuteAsync(world.TenantId, "DELETE FROM combo_items WHERE combo_id = @id", world.ComboId);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_row_version_is_rejected()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, _) = await OpenOrderAsync(client, world);

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new
        {
            comboId = world.ComboId,
            rowVersion = 999,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_role_without_TakeOrder_is_refused()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);
        var (orderId, rowVersion) = await OpenOrderAsync(client, world);

        // Same user, now presenting a Kitchen token: Kitchen carries only BumpKitchenLine.
        await AuthenticateAsync(client, world, "Kitchen");

        var response = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editing_a_combo_component_is_refused_so_the_bundle_total_survives()
    {
        var world = World.New();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, world, "Waiter");
        await SeedAsync(world);

        var (orderId, rowVersion) = await OpenOrderAsync(client, world);
        var added = await client.PostAsJsonAsync($"/orders/{orderId}/combos", new { comboId = world.ComboId, rowVersion });
        added.EnsureSuccessStatusCode();
        var addedBody = await added.Content.ReadFromJsonAsync<JsonElement>();
        var lineId = addedBody.GetProperty("lines").EnumerateArray().First().GetProperty("lineId").GetGuid();
        var nextRowVersion = addedBody.GetProperty("rowVersion").GetInt32();

        // The ordinary draft-edit path resets UnitPrice to the menu price. Left open, it would
        // turn an allocated 5.16 back into an 8.00 burger and the bundle would stop adding up.
        var response = await client.PatchAsJsonAsync($"/orders/{orderId}/lines/{lineId}", new
        {
            quantity = 2,
            modifierIds = Array.Empty<Guid>(),
            rowVersion = nextRowVersion,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var preview = await client.GetAsync($"/orders/{orderId}/bill-preview");
        preview.EnsureSuccessStatusCode();
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ComboPrice, previewBody.GetProperty("subtotal").GetDecimal());
    }

    // --- harness ----------------------------------------------------------

    private sealed record World(
        Guid TenantId, Guid BranchId, Guid WaiterId, Guid SectionId, Guid TableId,
        Guid GrillStationId, Guid FryerStationId, Guid BurgerId, Guid FriesId, Guid ComboId)
    {
        public static World New() => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

    private static async Task AuthenticateAsync(HttpClient client, World world, string role)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId = world.TenantId,
            branchId = world.BranchId,
            userId = world.WaiterId,
            role,
        });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", payload.GetProperty("accessToken").GetString());
    }

    private static async Task<(Guid OrderId, int RowVersion)> OpenOrderAsync(HttpClient client, World world)
    {
        var response = await client.PostAsJsonAsync($"/tables/{world.TableId}/orders", new { });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("orderId").GetGuid(), body.GetProperty("rowVersion").GetInt32());
    }

    private async Task<List<Guid>> QueryAsync(Guid tenantId, string sql, Guid orderId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("orderId", orderId);

        var results = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0)) results.Add(reader.GetGuid(0));
        }
        return results;
    }

    private async Task ExecuteAsync(Guid tenantId, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var affected = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        // Guards against a silent no-op: RLS hides rows when the tenant GUC is unset, and an
        // UPDATE that changed nothing would make the assertion that follows meaningless.
        Assert.True(affected > 0, $"Expected the statement to affect a row: {sql}");
    }

    private async Task GrantAsync(Guid tenantId, string role, string permission)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using var command = new NpgsqlCommand(
            "INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, @role, @permission)",
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("permission", permission);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedAsync(World w)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, w.TenantId);

        async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        await ExecAsync("INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')",
            ("id", w.TenantId), ("name", $"Tenant {w.TenantId:N}"));

        await ExecAsync("INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, 'Test Branch')",
            ("id", w.BranchId), ("tenantId", w.TenantId));

        // Tax and service off, so the bill-preview subtotal is the combo price and nothing else.
        await ExecAsync("INSERT INTO branch_settings (id, tenant_id, branch_id, currency, tax_rate, tax_applies, service_charge_rate, service_applies, tax_on_service) VALUES (@id, @tenantId, @branchId, 'EGP', 0, false, 0, false, false)",
            ("id", Guid.NewGuid()), ("tenantId", w.TenantId), ("branchId", w.BranchId));

        await ExecAsync("INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, 'Test Waiter', 'Waiter')",
            ("id", w.WaiterId), ("tenantId", w.TenantId));

        foreach (var permission in new[] { "TakeOrder", "FireOrder" })
        {
            await ExecAsync("INSERT INTO role_permissions (id, tenant_id, role, permission) VALUES (@id, @tenantId, 'Waiter', @permission)",
                ("id", Guid.NewGuid()), ("tenantId", w.TenantId), ("permission", permission));
        }

        await ExecAsync("INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Terrace')",
            ("id", w.SectionId), ("tenantId", w.TenantId), ("branchId", w.BranchId));

        await ExecAsync("INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, '12', 'Available')",
            ("id", w.TableId), ("tenantId", w.TenantId), ("sectionId", w.SectionId));

        await ExecAsync("INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Grill')",
            ("id", w.GrillStationId), ("tenantId", w.TenantId), ("branchId", w.BranchId));

        await ExecAsync("INSERT INTO stations (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, 'Fryer')",
            ("id", w.FryerStationId), ("tenantId", w.TenantId), ("branchId", w.BranchId));

        var categoryId = Guid.NewGuid();
        await ExecAsync("INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, 'Main', 1)",
            ("id", categoryId), ("tenantId", w.TenantId));

        await ExecAsync("INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, 'Burger', @price, 'Available')",
            ("id", w.BurgerId), ("tenantId", w.TenantId), ("categoryId", categoryId), ("stationId", w.GrillStationId), ("price", BurgerPrice));

        await ExecAsync("INSERT INTO menu_items (id, tenant_id, category_id, station_id, name, price, availability) VALUES (@id, @tenantId, @categoryId, @stationId, 'Fries', @price, 'Available')",
            ("id", w.FriesId), ("tenantId", w.TenantId), ("categoryId", categoryId), ("stationId", w.FryerStationId), ("price", FriesPrice));

        await ExecAsync("INSERT INTO combos (id, tenant_id, name, price) VALUES (@id, @tenantId, @name, @price)",
            ("id", w.ComboId), ("tenantId", w.TenantId), ("name", ComboName), ("price", ComboPrice));

        await ExecAsync("INSERT INTO combo_items (id, tenant_id, combo_id, item_id, quantity) VALUES (@id, @tenantId, @comboId, @itemId, 1)",
            ("id", Guid.NewGuid()), ("tenantId", w.TenantId), ("comboId", w.ComboId), ("itemId", w.BurgerId));

        await ExecAsync("INSERT INTO combo_items (id, tenant_id, combo_id, item_id, quantity) VALUES (@id, @tenantId, @comboId, @itemId, @quantity)",
            ("id", Guid.NewGuid()), ("tenantId", w.TenantId), ("comboId", w.ComboId), ("itemId", w.FriesId), ("quantity", FriesQuantity));

        await transaction.CommitAsync();
    }
}
