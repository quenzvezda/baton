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
builder.Services.AddAgentProtocol();

// Loops land per milestone (design spec §8 "Orchestrator loops", ADR-0008):
//   M0: PartitionMaintenanceLoop (daily, singleton)
//   M3: ReaperLoop (5 s, SKIP LOCKED), RetryPromoterLoop (1 s, SKIP LOCKED)
//   M4: TriggerLoop (1 s, SKIP LOCKED), AdmissionLoop (1 s, advisory lock per group),
//       LimitReconcilerLoop (5 min, singleton)
//   M6: AgingLoop (30 s, singleton)
builder.Services.AddHostedService<PartitionMaintenanceLoop>();

var app = builder.Build();

// M2: Register, Heartbeat (ADR-0013 credentials); M3: Claim, Start, Complete, AppendLogs.
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
