using Baton.Integration.Tests.Infrastructure;

namespace Baton.Integration.Tests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresSmokeTests(PostgresFixture pg)
{
    [Fact]
    public async Task Container_accepts_queries()
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT 1");

        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal(1, result);
    }
}
