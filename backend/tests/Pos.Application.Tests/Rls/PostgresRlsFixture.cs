using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Pos.Application.Tests.Rls;

/// <summary>
/// Spins up a real, hermetic Postgres 16 container once per RlsCollection (shared across
/// every test class in that collection, see RlsCollection), applies the InitialCreate
/// migration (as the admin/owner role) and the idempotent 002_create_app_role.sql script,
/// then exposes a connection string for the non-superuser pos_app role — the only role
/// tests are allowed to connect as.
/// </summary>
public sealed class PostgresRlsFixture : IAsyncLifetime
{
    private const string AdminUsername = "pos_owner";
    private const string AdminPassword = "pos_owner_dev_password_test";
    private const string AppUsername = "pos_app";
    private const string AppPassword = "pos_app_dev_password_test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("pos")
        .WithUsername(AdminUsername)
        .WithPassword(AdminPassword)
        .Build();

    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var adminConnectionString = _container.GetConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(adminConnectionString);
        await using (var adminDbContext = new PosDbContext(optionsBuilder.Options))
        {
            await adminDbContext.Database.MigrateAsync();
        }

        var roleScriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "002_create_app_role.sql");
        var roleScriptSql = (await File.ReadAllTextAsync(roleScriptPath))
            .Replace("pos_app_dev_password", AppPassword);

        await using var adminConnection = new NpgsqlConnection(adminConnectionString);
        await adminConnection.OpenAsync();
        await using (var command = new NpgsqlCommand(roleScriptSql, adminConnection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var appConnectionStringBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = AppUsername,
            Password = AppPassword,
        };
        AppConnectionString = appConnectionStringBuilder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
