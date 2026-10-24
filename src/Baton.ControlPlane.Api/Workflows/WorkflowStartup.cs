using Baton.Domain.Workflows;
using Baton.Persistence.Definitions;

namespace Baton.ControlPlane.Api.Workflows;

public static partial class WorkflowStartup
{
    /// <summary>
    /// Validates every definition and registers it before the API serves requests. Throws
    /// <see cref="WorkflowDefinitionException"/> on an invalid definition (WF-03) or a changed graph under
    /// a registered <c>key@version</c> (WF-04), so the process refuses to start.
    /// </summary>
    public static async Task RegisterWorkflowsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var workflows = services.GetRequiredService<WorkflowSet>().Workflows;
        var summary = await services.GetRequiredService<WorkflowRegistrar>().RegisterAsync(workflows, ct);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkflowStartup));
        LogRegistered(logger, workflows.Count, summary.Inserted, summary.Unchanged);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Count} workflow definitions ({Inserted} new, {Unchanged} unchanged)")]
    private static partial void LogRegistered(ILogger logger, int count, int inserted, int unchanged);
}
