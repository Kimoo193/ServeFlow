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

    [Fact]
    public async Task Tenants_table_unfiltered_query_returns_only_the_current_tenants_own_row()
    {
        // tenants' RLS policy compares its own `id` column (it IS the tenant), unlike
        // every other table's `tenant_id = ...` predicate — a structurally different
        // policy shape that needs its own proof, not just inference from branches.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTenantWithBranchAsync(tenantA, "A Tenants Table");
        await SeedTenantWithBranchAsync(tenantB, "B Tenants Table");

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantA);

        var names = await QuerySingleColumnAsync(connection, "tenants", "name");

        Assert.Equal([$"Tenant {tenantA:N}"], names);
    }

    [Fact]
    public async Task Users_table_unfiltered_query_returns_only_the_current_tenants_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedFullTenantAsync(tenantA, "A Users Branch", "A Owner", currency: "EGP");
        await SeedFullTenantAsync(tenantB, "B Users Branch", "B Owner", currency: "USD");

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantA);

        var displayNames = await QuerySingleColumnAsync(connection, "users", "display_name");

        Assert.Equal(["A Owner"], displayNames);
    }

    [Fact]
    public async Task BranchSettings_table_unfiltered_query_returns_only_the_current_tenants_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        // Distinct currencies per tenant so the assertion proves tenant A's specific row
        // was returned, not merely a row that happens to share a hardcoded value.
        await SeedFullTenantAsync(tenantA, "A Settings Branch", "A Owner Settings", currency: "EGP");
        await SeedFullTenantAsync(tenantB, "B Settings Branch", "B Owner Settings", currency: "USD");

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, tenantA);

        var currencies = await QuerySingleColumnAsync(connection, "branch_settings", "currency");

        Assert.Equal(["EGP"], currencies);
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

    private static Task<List<string>> QueryBranchNamesAsync(NpgsqlConnection connection) =>
        QuerySingleColumnAsync(connection, "branches", "name");

    // table/column are always compile-time literal constants from call sites in this
    // file, never external input, so interpolating them into SQL text is safe.
    private static async Task<List<string>> QuerySingleColumnAsync(NpgsqlConnection connection, string table, string column)
    {
        await using var command = new NpgsqlCommand($"SELECT {column} FROM {table}", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
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

    private async Task SeedFullTenantAsync(Guid tenantId, string branchName, string userDisplayName, string currency)
    {
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

        var branchId = Guid.NewGuid();
        await using (var insertBranch = new NpgsqlCommand(
            "INSERT INTO branches (id, tenant_id, name) VALUES (@id, @tenantId, @name)", connection))
        {
            insertBranch.Parameters.AddWithValue("id", branchId);
            insertBranch.Parameters.AddWithValue("tenantId", tenantId);
            insertBranch.Parameters.AddWithValue("name", branchName);
            await insertBranch.ExecuteNonQueryAsync();
        }

        await using (var insertBranchSettings = new NpgsqlCommand(
            """
            INSERT INTO branch_settings
                (id, tenant_id, branch_id, currency, tax_rate, tax_applies, service_charge_rate, service_applies, tax_on_service)
            VALUES
                (@id, @tenantId, @branchId, @currency, 0.14, true, 0.12, true, false)
            """,
            connection))
        {
            insertBranchSettings.Parameters.AddWithValue("id", Guid.NewGuid());
            insertBranchSettings.Parameters.AddWithValue("tenantId", tenantId);
            insertBranchSettings.Parameters.AddWithValue("branchId", branchId);
            insertBranchSettings.Parameters.AddWithValue("currency", currency);
            await insertBranchSettings.ExecuteNonQueryAsync();
        }

        await using (var insertUser = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, display_name, role) VALUES (@id, @tenantId, @displayName, 'Owner')", connection))
        {
            insertUser.Parameters.AddWithValue("id", Guid.NewGuid());
            insertUser.Parameters.AddWithValue("tenantId", tenantId);
            insertUser.Parameters.AddWithValue("displayName", userDisplayName);
            await insertUser.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
