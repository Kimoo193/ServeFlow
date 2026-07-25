using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Application.Tests.Rls;
using Pos.Infrastructure.Persistence;

namespace Pos.Application.Tests.Persistence;

/// <summary>
/// Covers the seed's idempotency against a database that already exists.
/// </summary>
/// <remarks>
/// The permission matrix was originally seeded only when the tenant had no grants at
/// all. Every automated test runs against a container built from scratch, so that
/// all-or-nothing guard always looked correct here while silently failing on a real
/// developer's long-lived database: a permission added after that database was first
/// seeded never arrived, and the roles depending on it 403'd. These tests seed twice
/// on purpose — the second pass is the one that matters.
/// </remarks>
[Collection(RlsCollection.Name)]
public sealed class DevSeederTests(PostgresRlsFixture fixture)
{
    [Fact]
    public async Task SeedAsync_grants_a_permission_missing_from_an_already_seeded_database()
    {
        await SeedAsync();

        // Stand in for a database seeded before Permission.BumpKitchenLine existed.
        await DeleteGrantAsync("Kitchen", "BumpKitchenLine");
        Assert.False(await HasGrantAsync("Kitchen", "BumpKitchenLine"));

        await SeedAsync();

        Assert.True(await HasGrantAsync("Kitchen", "BumpKitchenLine"));
    }

    [Fact]
    public async Task SeedAsync_run_twice_does_not_duplicate_grants()
    {
        await SeedAsync();
        await SeedAsync();

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        // SET LOCAL is scoped to a transaction and silently does nothing outside one —
        // without this the RLS policy would hide every row and the count would be a
        // vacuous zero.
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, DevSeeder.TenantId);

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM (
                SELECT role, permission FROM role_permissions
                WHERE tenant_id = @tenantId
                GROUP BY role, permission
                HAVING count(*) > 1
            ) duplicates
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", DevSeeder.TenantId);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task SeedAsync()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql(fixture.AppConnectionString)
            .Options;
        await using var dbContext = new PosDbContext(options);
        await DevSeeder.SeedAsync(dbContext);
    }

    private async Task DeleteGrantAsync(string role, string permission)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, DevSeeder.TenantId);

        await using var command = new NpgsqlCommand(
            "DELETE FROM role_permissions WHERE tenant_id = @tenantId AND role = @role AND permission = @permission",
            connection);
        command.Parameters.AddWithValue("tenantId", DevSeeder.TenantId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("permission", permission);
        var deleted = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        // Guards against the assertion passing vacuously: if RLS or a missing tenant
        // context hid the row, nothing was deleted and the test proves nothing.
        Assert.Equal(1, deleted);
    }

    private async Task<bool> HasGrantAsync(string role, string permission)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RlsTestSql.SetTenantAsync(connection, DevSeeder.TenantId);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM role_permissions WHERE tenant_id = @tenantId AND role = @role AND permission = @permission",
            connection);
        command.Parameters.AddWithValue("tenantId", DevSeeder.TenantId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("permission", permission);

        return (long)(await command.ExecuteScalarAsync())! > 0;
    }
}
