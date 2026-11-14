using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Health;
using Baton.Persistence;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// ORC-06 (M6 design.md decision 9): every 30 s on one replica, raises the effective priority of queued tasks by
/// one point per aging step waited, up to the cap. No NOTIFY: aging never makes a task newly eligible.
/// </summary>
public sealed partial class AgingLoop(
    NpgsqlDataSource dataSource, IOptions<DispatchOptions> options, OrchestratorMetrics metrics, ILogger<AgingLoop> logger)
    : SingletonLoop(dataSource, logger)
{
    protected override long LockId => AdvisoryLocks.Aging;

    protected override TimeSpan Interval => options.Value.AgingInterval;

    protected override async Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var aged = await PriorityAging.AgeAsync(connection, transaction, options.Value.Aging, ct);
        if (aged == 0)
            return;

        metrics.TasksAged(aged);
        LogAged(Logger, aged);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Raised the effective priority of {Count} queued tasks")]
    private static partial void LogAged(ILogger logger, int count);
}
