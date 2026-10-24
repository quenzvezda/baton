using Npgsql;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// A loop that runs on one orchestrator replica at a time (ADR-0008). Every tick opens a transaction
/// and takes <c>pg_try_advisory_xact_lock(LockId)</c>; replicas that miss the lock skip the tick. The
/// first tick runs at startup, then one per <see cref="Interval"/>.
/// </summary>
public abstract partial class SingletonLoop(NpgsqlDataSource dataSource, ILogger logger) : BackgroundService
{
    protected ILogger Logger { get; } = logger;

    /// <summary>A constant from <c>Baton.Persistence.AdvisoryLocks</c>.</summary>
    protected abstract long LockId { get; }

    protected abstract TimeSpan Interval { get; }

    /// <summary>Runs inside the locked transaction, which commits when this returns.</summary>
    protected abstract Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed tick must not stop the loop; the next tick retries.
                LogTickFailed(Logger, ex, GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick; <c>false</c> when another replica holds the lock and the tick was skipped.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@lockId)", connection, transaction))
        {
            lockCmd.Parameters.AddWithValue("lockId", LockId);
            if (await lockCmd.ExecuteScalarAsync(ct) is not true)
                return false;
        }

        await TickAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Loop} tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception, string loop);
}
