using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Every 1 s on every replica (ORC-02, ADR-0008, ADR-0009): admits Pending runs whose limits all have room, by
/// priority, passing over runs that do not fit (<see cref="RunAdmission"/>). One replica admits a group at a
/// time; the others skip it for that tick.
/// </summary>
public sealed partial class AdmissionLoop(RunAdmission admission, IOptions<DispatchOptions> options, ILogger<AdmissionLoop> logger)
    : PeriodicLoop(logger)
{
    protected override TimeSpan Interval => options.Value.AdmissionInterval;

    protected internal override async Task TickAsync(CancellationToken ct)
    {
        var pass = await admission.AdmitAsync(ct);
        if (pass.Admitted > 0)
            LogAdmitted(Logger, pass.Admitted, pass.PassedOver);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Admitted {Admitted} runs, passed over {PassedOver}")]
    private static partial void LogAdmitted(ILogger logger, int admitted, int passedOver);
}
