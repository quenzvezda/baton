using Baton.Persistence.History;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Baton.Orchestrator.Health;

/// <summary>
/// Degraded when a history table has fewer than <see cref="MinFutureMonths"/> monthly partitions after
/// the current month (ADR-0011): the daily maintenance has stalled and inserts will fail once the
/// window runs out. Degraded keeps <c>/healthz</c> at HTTP 200, so probes do not restart the process.
/// </summary>
internal sealed class HistoryPartitionsHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public const int MinFutureMonths = 2;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var shortTables = await HistoryRetention.FindTablesShortOfFuturePartitionsAsync(
            connection, transaction: null, MinFutureMonths, cancellationToken);

        return shortTables.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"Fewer than {MinFutureMonths} future monthly partitions: {string.Join(", ", shortTables)}");
    }
}
