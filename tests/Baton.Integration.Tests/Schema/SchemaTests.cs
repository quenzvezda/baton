using Baton.Integration.Tests.Infrastructure;
using Npgsql;

namespace Baton.Integration.Tests.Schema;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class SchemaTests(PostgresFixture pg)
{
    private const string BaselineDatabase = "baseline_reference";

    /// <summary>
    /// Deliberate additions on top of the design-spec schema, applied to the reference database so the
    /// comparison stays exact. Each one is backed by an OpenSpec task or ADR.
    /// </summary>
    private const string BaselineAdditions = """
        -- M0 task 2.2: keep autovacuum up with the constant updates on task_runs.
        ALTER TABLE task_runs SET (autovacuum_vacuum_scale_factor = 0.01);
        """;

    /// <summary>
    /// One line per table, column, index, constraint and sequence in <c>public</c>, rendered by Postgres
    /// itself so both sides are normalized the same way. Partitions (created ahead by
    /// <c>ensure_history_partitions</c>) and EF's history table are left out. UNIQUE constraints are
    /// compared through their index: EF models them as unique indexes, which enforce the same rule and
    /// serve <c>ON CONFLICT (columns)</c> the same way.
    /// </summary>
    private const string DescribeCatalog = """
        WITH rel AS (
          SELECT c.oid, c.relname, c.relkind, c.reloptions
          FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition
            AND c.relname <> '__EFMigrationsHistory'
        )
        SELECT format('table %s kind=%s partition=%s options=%s',
                      relname, relkind, pg_get_partkeydef(oid), reloptions)
        FROM rel
        UNION ALL
        SELECT format('column %s.%s %s%s%s%s', r.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                      CASE WHEN a.attnotnull THEN ' not null' ELSE '' END,
                      ' default ' || pg_get_expr(d.adbin, d.adrelid),
                      CASE a.attidentity WHEN 'a' THEN ' identity always'
                                         WHEN 'd' THEN ' identity by default' ELSE '' END)
        FROM rel r
        JOIN pg_attribute a ON a.attrelid = r.oid AND a.attnum > 0 AND NOT a.attisdropped
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        UNION ALL
        SELECT 'index ' || pg_get_indexdef(i.indexrelid)
        FROM rel r JOIN pg_index i ON i.indrelid = r.oid
        UNION ALL
        SELECT format('constraint %s %s %s', r.relname, con.conname, pg_get_constraintdef(con.oid))
        FROM rel r JOIN pg_constraint con ON con.conrelid = r.oid
        WHERE con.contype IN ('p', 'f', 'c')
        UNION ALL
        SELECT 'sequence ' || c.relname
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public' AND c.relkind = 'S'
        """;

    [Fact]
    public async Task Schema_matches_baseline()
    {
        var expected = await DescribeBaselineAsync();
        var actual = await DescribeAsync(pg.DataSource);

        var missingFromMigration = expected.Except(actual);
        var notInBaseline = actual.Except(expected);

        Assert.NotEmpty(expected);
        Assert.Multiple(
            () => Assert.Empty(missingFromMigration),
            () => Assert.Empty(notInBaseline));
    }

    private async Task<HashSet<string>> DescribeBaselineAsync()
    {
        await using (var cmd = pg.DataSource.CreateCommand(
            $"DROP DATABASE IF EXISTS {BaselineDatabase}; CREATE DATABASE {BaselineDatabase};"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = BaselineDatabase };
        await using var baseline = NpgsqlDataSource.Create(builder.ConnectionString);

        // 0001_baseline.sql is the design-spec schema; later files are the reference for later migrations.
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Schema"), "*.sql").Order(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var sql = await File.ReadAllTextAsync(path);
            if (Path.GetFileName(path) == "0001_baseline.sql")
                sql += BaselineAdditions;
            await using var cmd = baseline.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        return await DescribeAsync(baseline);
    }

    private static async Task<HashSet<string>> DescribeAsync(NpgsqlDataSource dataSource)
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = dataSource.CreateCommand(DescribeCatalog);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return lines;
    }
}
