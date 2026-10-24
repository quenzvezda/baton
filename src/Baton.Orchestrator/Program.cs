var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();

// Loops land per milestone (design spec §8 "Orchestrator loops", ADR-0008):
//   M3: ReaperLoop (5 s, SKIP LOCKED), RetryPromoterLoop (1 s, SKIP LOCKED)
//   M4: TriggerLoop (1 s, SKIP LOCKED), AdmissionLoop (1 s, advisory lock per group),
//       LimitReconcilerLoop (5 min, singleton)
//   M6: AgingLoop (30 s, singleton)
// M2/M3: app.MapGrpcService<AgentService>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "orchestrator" }));

app.Run();
