using Baton.Integration.Tests.Infrastructure;
using Baton.Orchestrator.Loops;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Baton.Integration.Tests.Orchestrator;

/// <summary>ADR-0008: singleton loops coordinate replicas with a transaction-scoped advisory lock.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SingletonLoopTests(PostgresFixture pg)
{
    [Fact]
    public async Task Second_replica_skips_the_tick_while_the_first_holds_the_lock()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new GatedLoop(pg.DataSource, release.Task);
        var second = new GatedLoop(pg.DataSource, Task.CompletedTask);

        var firstRun = first.RunOnceAsync(CancellationToken.None);
        await first.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await second.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, second.Ticks);

        release.SetResult();
        Assert.True(await firstRun);
        Assert.True(await second.RunOnceAsync(CancellationToken.None)); // lock ends with the first's transaction
        Assert.Equal(1, second.Ticks);
    }

    [Fact]
    public async Task Partition_maintenance_tick_runs_against_the_migrated_schema()
    {
        var loop = new PartitionMaintenanceLoop(pg.DataSource, NullLogger<PartitionMaintenanceLoop>.Instance);

        Assert.True(await loop.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>Signals when its tick starts, then waits for <c>gate</c> while holding the lock.</summary>
    private sealed class GatedLoop(NpgsqlDataSource dataSource, Task gate)
        : SingletonLoop(dataSource, NullLogger.Instance)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public int Ticks { get; private set; }

        protected override long LockId => 0x7465_7374_0000_0001; // "test", outside AdvisoryLocks' prefix

        protected override TimeSpan Interval => TimeSpan.FromHours(1);

        protected override async Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            Ticks++;
            _entered.TrySetResult();
            await gate.WaitAsync(ct);
        }
    }
}
