using Baton.Orchestrator.Dispatch;
using Baton.Persistence;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Every 5 minutes on one replica (M3 design.md decision 8): recomputes <c>concurrency_limits.in_use</c>
/// from live runs and tasks. A correction means a bug somewhere, so each one is a warning.
/// </summary>
public sealed partial class LimitReconcilerLoop(
    NpgsqlDataSource dataSource, IOptions<DispatchOptions> options, ILogger<LimitReconcilerLoop> logger)
    : SingletonLoop(dataSource, logger)
{
    protected override long LockId => AdvisoryLocks.LimitReconciler;

    protected override TimeSpan Interval => options.Value.LimitReconcilerInterval;

    protected override async Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        foreach (var correction in await LimitReconciler.ReconcileAsync(connection, transaction, ct))
            LogCorrected(Logger, correction.Key, correction.Was, correction.Now);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Limit {Key} counted {Was} in use, live rows say {Now}; corrected")]
    private static partial void LogCorrected(ILogger logger, string key, int was, int now);
}
