using Npgsql;

namespace Pos.Application.Tests.Rls;

/// <summary>
/// Slice 1 mandatory acceptance test (tasks.md): proves tenant A cannot read tenant B's
/// rows via RLS, even with a crafted query — connecting only as the non-superuser
/// pos_app role (a superuser would bypass RLS and invalidate the test).
/// </summary>
public sealed class RlsIsolationTests(PostgresRlsFixture fixture) : IClassFixture<PostgresRlsFixture>
{
    [Fact]
    public async Task Unfiltered_query_returns_only_the_current_tenants_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A Main");
        await SeedTenantWithBranchAsync(tenantB, "B Main");

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantA);

        var names = await QueryBranchNamesAsync(connection);

        Assert.Equal(["A Main"], names);
    }

    [Fact]
    public async Task Crafted_filter_naming_another_tenant_still_returns_nothing()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A Crafted");
        await SeedTenantWithBranchAsync(tenantB, "B Crafted");

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantA);

        // An attacker-crafted query explicitly asking for tenant B's rows by id — RLS
        // still intersects with the session's own tenant, so this returns nothing,
        // proving isolation does not depend on the application ever writing a correct
        // WHERE clause.
        await using var command = new NpgsqlCommand(
            "SELECT name FROM branches WHERE tenant_id = @tenantB", connection);
        command.Parameters.AddWithValue("tenantB", tenantB);
        var result = await command.ExecuteScalarAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task Unset_tenant_context_fails_closed_with_zero_rows()
    {
        var tenantA = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A NoContext");

        // No SET LOCAL app.tenant_id at all — current_setting(..., true) returns NULL,
        // and `tenant_id = NULL` is never true, so this must return zero rows, never
        // every tenant's rows (fail closed, not fail open).
        await using var connection = await OpenAppConnectionAsync();
        var names = await QueryBranchNamesAsync(connection);

        Assert.Empty(names);
    }

    [Fact]
    public async Task Same_physical_connection_reused_across_transactions_does_not_leak_tenant_id()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A Solo");
        await SeedTenantWithBranchAsync(tenantB, "B Solo");

        // One physical connection, reused for two sequential "requests" — proves SET
        // LOCAL is discarded at COMMIT regardless of connection reuse, not merely that
        // a fresh connection happens to start clean.
        await using var connection = await OpenAppConnectionAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, tenantA);
            Assert.Equal(["A Solo"], await QueryBranchNamesAsync(connection));
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetTenantAsync(connection, tenantB);
            Assert.Equal(["B Solo"], await QueryBranchNamesAsync(connection));
            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task Pooled_connection_handed_to_a_different_tenant_does_not_leak_app_tenant_id()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A Pooled");
        await SeedTenantWithBranchAsync(tenantB, "B Pooled");

        // "Request 1": open, use, commit, and dispose a connection as tenant A —
        // disposing returns the physical socket to Npgsql's pool.
        await using (var connection = await OpenAppConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, tenantA);
            Assert.Equal(["A Pooled"], await QueryBranchNamesAsync(connection));
            await transaction.CommitAsync();
        }

        // "Request 2": a brand new connection object on the same connection string —
        // with an otherwise-idle pool, Npgsql will very likely hand back the exact
        // physical socket just released above.
        await using (var connection = await OpenAppConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, tenantB);
            Assert.Equal(["B Pooled"], await QueryBranchNamesAsync(connection));
            await transaction.CommitAsync();
        }
    }

    private async Task<NpgsqlConnection> OpenAppConnectionAsync()
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand(
            $"SET LOCAL app.tenant_id = '{tenantId:D}'", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> QueryBranchNamesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT name FROM branches", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task SeedTenantWithBranchAsync(Guid tenantId, string branchName)
    {
        // Seeding runs as pos_app too (never a superuser/admin bypass), so it must
        // set its own tenant before inserting — exactly like DevSeeder does.
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantId);

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
            insertBranch.Parameters.AddWithValue("id", Guid.NewGuid());
            insertBranch.Parameters.AddWithValue("tenantId", tenantId);
            insertBranch.Parameters.AddWithValue("name", branchName);
            await insertBranch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
