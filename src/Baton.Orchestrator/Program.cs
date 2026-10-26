using Baton.Orchestrator.Agents;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Persistence;
using Baton.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddBatonTelemetry("orchestrator");
builder.Services.AddBatonPersistence(builder.Configuration.GetConnectionString("Baton"));
builder.Services.AddBatonListenDataSource(builder.Configuration.GetConnectionString("BatonListen"));
builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres");
builder.Services.AddAgentProtocol(builder.Configuration);

// Loops land per milestone (design spec §8 "Orchestrator loops", ADR-0008):
//   M0: PartitionMaintenanceLoop (daily, singleton)
//   M3: ReaperLoop (5 s, SKIP LOCKED), RetryPromoterLoop (1 s, SKIP LOCKED),
//       LimitReconcilerLoop (5 min, singleton), TasksReadyListener (LISTEN for claims)
//   M4: TriggerLoop (1 s, SKIP LOCKED), AdmissionLoop (1 s, advisory lock per group)
//   M6: AgingLoop (30 s, singleton)
builder.Services.AddHostedService<PartitionMaintenanceLoop>();
builder.Services.AddDispatchLoops();

var app = builder.Build();

// Register, Heartbeat (M2, ADR-0013 credentials); Claim, Start, Complete, AppendLogs (M3).
app.MapAgentProtocol();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status == HealthStatus.Healthy ? "ok" : "unhealthy",
        service = "orchestrator",
        checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
    }),
});

app.Run();
