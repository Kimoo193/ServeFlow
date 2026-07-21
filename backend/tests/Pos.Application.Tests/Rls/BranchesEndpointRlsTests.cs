using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Pos.Application.Tests.Rls;

/// <summary>
/// Slice 1 mandatory acceptance test (tasks.md): the same isolation proof as
/// RlsIsolationTests, but driven through real HTTP requests against the actual
/// Pos.Api host — POST /dev/token to mint a real JWT, then GET /branches with it —
/// so the whole pipeline (mint, validate, claims, tenant context, RLS) is exercised,
/// not just the database layer in isolation.
/// </summary>
public sealed class BranchesEndpointRlsTests(PostgresRlsFixture fixture) : IClassFixture<PostgresRlsFixture>
{
    [Fact]
    public async Task Get_branches_over_http_returns_only_the_authenticated_tenants_rows()
    {
        var tenantAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var branchBId = Guid.NewGuid();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = fixture.AppConnectionString,
                    // Never resolved by this test (nothing here calls /health), but must
                    // be a syntactically valid value for AddInfrastructure to configure.
                    ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=100",
                    ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-production-use-32b",
                });
            });
        });

        using var client = factory.CreateClient();

        var tokenA = await GetDevTokenAsync(client, tenantAId, branchAId);
        var tokenB = await GetDevTokenAsync(client, tenantBId, branchBId);

        await SeedBranchAsync(tenantAId, branchAId, "A Endpoint Only");
        await SeedBranchAsync(tenantBId, branchBId, "B Endpoint Only");

        Assert.Equal(["A Endpoint Only"], await GetBranchNamesAsync(client, tokenA));
        Assert.Equal(["B Endpoint Only"], await GetBranchNamesAsync(client, tokenB));

        // Order re-checked to rule out any hidden ordering-dependent state leaking
        // between requests on the same HttpClient/connection pool.
        Assert.Equal(["A Endpoint Only"], await GetBranchNamesAsync(client, tokenA));
    }

    private static async Task<string> GetDevTokenAsync(HttpClient client, Guid tenantId, Guid branchId)
    {
        var response = await client.PostAsJsonAsync("/dev/token", new
        {
            tenantId,
            branchId,
            userId = Guid.NewGuid(),
            role = "Manager",
        });
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedBranchAsync(Guid tenantId, Guid branchId, string branchName)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var setTenant = new NpgsqlCommand($"SET LOCAL app.tenant_id = '{tenantId:D}'", connection))
        {
            await setTenant.ExecuteNonQueryAsync();
        }

        await using (var insertTenant = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, type) VALUES (@id, @name, 'Restaurant')", connection))
        {
            insertTenant.Parameters.AddWithValue("id", tenantId);
            insertTenant.Parameters.AddWithValue("name", $"Tenant {tenantId:N}");
            await insertTenant.ExecuteNonQueryAsync();
        }

        await using (var insertBranch = new NpgsqlCommand(
            "INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, @name)", connection))
        {
            insertBranch.Parameters.AddWithValue("id", branchId);
            insertBranch.Parameters.AddWithValue("tenantId", tenantId);
            insertBranch.Parameters.AddWithValue("name", branchName);
            await insertBranch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<List<string>> GetBranchNamesAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/branches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var branches = await response.Content.ReadFromJsonAsync<List<BranchDto>>();
        return branches!.Select(b => b.Name).ToList();
    }

    private sealed record BranchDto(Guid Id, string Name);
}
