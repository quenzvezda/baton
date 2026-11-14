using Baton.Orchestrator.Agents;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Orchestrator.Notifications;
using Baton.Persistence;
using Baton.Persistence.Security;
using Baton.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddBatonTelemetry("orchestrator").AddBatonAspNetCoreTelemetry().AddBatonNpgsqlTelemetry();
builder.Services.AddBatonPersistence(builder.Configuration.GetConnectionString("Baton"));
builder.Services.AddBatonListenDataSource(builder.Configuration.GetConnectionString("BatonListen"));
// Notification secrets (ADR-0018): the key ring shared with the Control Plane API.
builder.Services.AddBatonDataProtection(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres")
    .AddCheck<HistoryPartitionsHealthCheck>("history-partitions");
builder.Services.AddAgentProtocol(builder.Configuration);

// Loops land per milestone (design spec §8 "Orchestrator loops", ADR-0008):
//   M0: PartitionMaintenanceLoop (daily, singleton; G1 adds the 90-day retention)
//   M3: ReaperLoop (5 s, SKIP LOCKED; M4 adds cancel grace and run deadlines), RetryPromoterLoop (1 s, SKIP LOCKED),
//       LimitReconcilerLoop (5 min, singleton), TasksReadyListener (LISTEN for claims)
//   M4: TriggerLoop (1 s, SKIP LOCKED), AdmissionLoop (1 s, advisory lock per group)
//   M6: AgingLoop (30 s, singleton; registered with the dispatch loops)
builder.Services.AddOrchestratorMetrics();
builder.Services.AddHostedService<PartitionMaintenanceLoop>();
builder.Services.AddDispatchLoops();
//   M7: NotificationLoop (5 s, SKIP LOCKED; OBS-05), QueueMetricsLoop (15 s; with the dispatch loops)
builder.Services.AddNotifications(builder.Configuration);

var app = builder.Build();

// Register, Heartbeat (M2, ADR-0013 credentials); Claim, Start, Complete, AppendLogs (M3).
app.MapAgentProtocol();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status switch
        {
            HealthStatus.Healthy => "ok",
            HealthStatus.Degraded => "degraded",
            _ => "unhealthy",
        },
        service = "orchestrator",
        checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        details = report.Entries.Where(e => e.Value.Description is not null).ToDictionary(e => e.Key, e => e.Value.Description),
    }),
});

app.Run();
