using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pos.Application.Tests.Rls;

namespace Pos.Application.Tests;

[Collection(RlsCollection.Name)]
public sealed class AvailabilityBroadcastTests(PostgresRlsFixture fixture)
{
    private const string StationName = "Grill";
    private const string MenuItemName = "Burger";
    private const decimal MenuItemPrice = 15.50m;
    private const string CategoryName = "Main";

    [Fact]
    public async Task Availability_change_broadcasts_to_branch_group()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);

        var server = factory.Server;

        var availabilityChanged = new TaskCompletionSource<JsonElement>();

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/posHub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token)!;
            })
            .Build();

        connection.On<JsonElement>("AvailabilityChanged", data =>
        {
            availabilityChanged.TrySetResult(data);
        });

        await connection.StartAsync();
        await connection.SendAsync("JoinBranchGroup", branchId);
        await Task.Delay(200);

        // Change availability from Available to OutOfStock
        var updateBody = new
        {
            categoryId,
            stationId,
            name = MenuItemName,
            price = MenuItemPrice,
            availability = "OutOfStock",
        };
        var putResponse = await client.PutAsJsonAsync($"/menu-items/{menuItemId}", updateBody);
        putResponse.EnsureSuccessStatusCode();

        var result = await availabilityChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(menuItemId, result.GetProperty("itemId").GetGuid());
        Assert.Equal("OutOfStock", result.GetProperty("availability").GetString());

        await connection.StopAsync();
    }

    [Fact]
    public async Task Same_availability_does_not_broadcast()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var waiterId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var menuItemId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, waiterId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedFullAsync(tenantId, branchId, waiterId, sectionId, tableId, stationId, categoryId, menuItemId);

        var server = factory.Server;

        var availabilityChanged = new TaskCompletionSource<JsonElement>();

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/posHub", options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token)!;
            })
            .Build();

        connection.On<JsonElement>("AvailabilityChanged", data =>
        {
            availabilityChanged.TrySetResult(data);
        });

        await connection.StartAsync();
        await connection.SendAsync("JoinBranchGroup", branchId);
        await Task.Delay(200);

        // Update with the same availability (Available -> Available) — no broadcast expected
        var updateBody = new
        {
            categoryId,
            stationId,
            name = MenuItemName,
            price = MenuItemPrice,
            availability = "Available",
        };
        var putResponse = await client.PutAsJsonAsync($"/menu-items/{menuItemId}", updateBody);
        putResponse.EnsureSuccessStatusCode();

        // Assert no broadcast arrived within a short timeout
        var completed = await Task.WhenAny(availabilityChanged.Task, Task.Delay(2000));
        Assert.NotSame(availabilityChanged.Task, completed);
        Assert.False(availabilityChanged.Task.IsCompleted);

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

    private async Task SeedFullAsync(
        Guid tenantId, Guid branchId, Guid waiterId,
        Guid sectionId, Guid tableId,
        Guid stationId, Guid categoryId, Guid menuItemId)
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

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO categories (id, tenant_id, name, sort_order) VALUES (@id, @tenantId, @name, 1)", connection))
        {
            cmd.Parameters.AddWithValue("id", categoryId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", CategoryName);
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
