namespace Pos.Application.Tests.Rls;

/// <summary>
/// Shares one PostgresRlsFixture (one Testcontainers Postgres, one migration, one
/// role-script run) across every test class in this collection, instead of each class's
/// own IClassFixture spinning up an independent container. xUnit runs tests within a
/// collection sequentially, but each test still seeds its own randomly-generated tenant
/// ids, so sharing the database introduces no cross-test coupling.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RlsCollection : ICollectionFixture<PostgresRlsFixture>
{
    public const string Name = "Rls";
}
