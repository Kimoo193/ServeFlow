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
public sealed class DiningAreaAdminTests(PostgresRlsFixture fixture)
{
    [Fact]
    public async Task Edit_section_name_via_PUT()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedSectionAsync(tenantId, branchId, sectionId);

        var putResponse = await client.PutAsJsonAsync($"/sections/{sectionId}", new { name = "Updated Terrace" });
        Assert.Equal(200, (int)putResponse.StatusCode);

        var getResponse = await client.GetAsync("/sections");
        getResponse.EnsureSuccessStatusCode();
        var sections = await getResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var section = sections!.Single(s => s.GetProperty("id").GetGuid() == sectionId);
        Assert.Equal("Updated Terrace", section.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Delete_empty_section_returns_204()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedSectionAsync(tenantId, branchId, sectionId);

        var response = await client.DeleteAsync($"/sections/{sectionId}");
        Assert.Equal(204, (int)response.StatusCode);
    }

    [Fact]
    public async Task Delete_section_with_table_returns_409()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedSectionAsync(tenantId, branchId, sectionId);
        await SeedTableAsync(tenantId, sectionId, tableId);

        var response = await client.DeleteAsync($"/sections/{sectionId}");
        Assert.Equal(409, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Section has tables", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Edit_table_via_PUT()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var section1Id = Guid.NewGuid();
        var section2Id = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedSectionsAsync(tenantId, branchId, (section1Id, "Section 1"), (section2Id, "Section 2"));
        await SeedTableAsync(tenantId, section1Id, tableId);

        var putResponse = await client.PutAsJsonAsync($"/tables/{tableId}", new { sectionId = section2Id, label = "New Label" });
        Assert.Equal(200, (int)putResponse.StatusCode);

        var getResponse = await client.GetAsync("/tables");
        getResponse.EnsureSuccessStatusCode();
        var tables = await getResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var table = tables!.Single(t => t.GetProperty("id").GetGuid() == tableId);
        Assert.Equal("New Label", table.GetProperty("label").GetString());
        Assert.Equal(section2Id.ToString(), table.GetProperty("sectionId").GetString());
    }

    [Fact]
    public async Task Delete_table_with_no_order_returns_204()
    {
        var tenantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var managerId = Guid.NewGuid();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var token = await GetDevTokenAsync(client, tenantId, branchId, managerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedTenantAsync(tenantId, branchId, managerId);
        await SeedSectionAsync(tenantId, branchId, sectionId);
        await SeedTableAsync(tenantId, sectionId, tableId);

        var response = await client.DeleteAsync($"/tables/{tableId}");
        Assert.Equal(204, (int)response.StatusCode);
    }

    [Fact]
    public async Task Delete_table_with_open_order_returns_409()
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

        await SeedTenantAsync(tenantId, branchId, waiterId);
        await SeedSectionAsync(tenantId, branchId, sectionId);
        await SeedTableAsync(tenantId, sectionId, tableId);

        // Open an order on the table
        var orderResponse = await client.PostAsync($"/tables/{tableId}/orders", null);
        orderResponse.EnsureSuccessStatusCode();

        var response = await client.DeleteAsync($"/tables/{tableId}");
        Assert.Equal(409, (int)response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Table has an active order", body.GetProperty("title").GetString());
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

    private async Task SeedTenantAsync(Guid tenantId, Guid branchId, Guid userId)
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
            "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @name, @role)", connection))
        {
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", "Test User");
            cmd.Parameters.AddWithValue("role", "Manager");
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task SeedSectionAsync(Guid tenantId, Guid branchId, Guid sectionId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
        {
            cmd.Parameters.AddWithValue("id", sectionId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("branchId", branchId);
            cmd.Parameters.AddWithValue("name", "Terrace");
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task SeedSectionsAsync(Guid tenantId, Guid branchId, params (Guid Id, string Name)[] sections)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        foreach (var (id, name) in sections)
        {
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO sections (id, tenant_id, branch_id, name) VALUES (@id, @tenantId, @branchId, @name)", connection))
            {
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("tenantId", tenantId);
                cmd.Parameters.AddWithValue("branchId", branchId);
                cmd.Parameters.AddWithValue("name", name);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    private async Task SeedTableAsync(Guid tenantId, Guid sectionId, Guid tableId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await RlsTestSql.SetTenantAsync(connection, tenantId);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO dining_tables (id, tenant_id, section_id, label, status) VALUES (@id, @tenantId, @sectionId, @label, 'Available')", connection))
        {
            cmd.Parameters.AddWithValue("id", tableId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("sectionId", sectionId);
            cmd.Parameters.AddWithValue("label", "1");
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
