using Baton.Integration.Tests.Infrastructure;
using Npgsql;

namespace Baton.Integration.Tests.Schema;

/// <summary>ADR-0011: history tables are partitioned by month, created 3 months ahead.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HistoryPartitionTests(PostgresFixture pg)
{
    public static TheoryData<string> HistoryTables => ["task_attempts", "task_logs", "run_events"];

    [Theory]
    [MemberData(nameof(HistoryTables))]
    public async Task Migrated_database_accepts_history_rows_up_to_three_months_ahead(string table)
    {
        for (var months = 0; months <= 3; months++)
            await InsertAndRollBackAsync(table, months);
    }

    [Theory]
    [MemberData(nameof(HistoryTables))]
    public async Task History_rows_beyond_the_created_partitions_are_rejected(string table)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAndRollBackAsync(table, months: 4));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Ensure_history_partitions_is_idempotent()
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT ensure_history_partitions()");

        var created = await cmd.ExecuteScalarAsync();

        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Ensure_history_partitions_extends_the_window()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(); // DDL is transactional: rolled back on dispose
        await using var cmd = new NpgsqlCommand("SELECT ensure_history_partitions(months_ahead => 4)", conn, tx);

        var created = await cmd.ExecuteScalarAsync();

        Assert.Equal(3, created); // one new month for each of the three history tables
    }

    /// <summary>Inserts a row stamped <c>now() + months</c> (Postgres time) and rolls it back.</summary>
    private async Task InsertAndRollBackAsync(string table, int months)
    {
        var sql = table switch
        {
            "task_attempts" => """
                INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at)
                VALUES (gen_random_uuid(), 1, gen_random_uuid(), 1, now() + make_interval(months => @months))
                """,
            "task_logs" => """
                INSERT INTO task_logs (task_run_id, attempt, seq, logged_at, level, message)
                VALUES (gen_random_uuid(), 1, 1, now() + make_interval(months => @months), 2, 'test')
                """,
            "run_events" => """
                INSERT INTO run_events (workflow_run_id, type, at)
                VALUES (gen_random_uuid(), 'test', now() + make_interval(months => @months))
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };

        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("months", months);

        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }
}
