using Npgsql;

namespace Pos.Application.Tests.Rls;

/// <summary>
/// Shared by every test in this folder that needs to set the RLS tenant GUC directly
/// on a raw <see cref="NpgsqlConnection"/> (production code goes through
/// TenantSessionSql/DatabaseFacade instead — these tests operate below EF Core to
/// control connection/transaction lifetime precisely).
/// </summary>
internal static class RlsTestSql
{
    public static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL app.tenant_id = '{tenantId:D}'", connection);
        await command.ExecuteNonQueryAsync();
    }
}
