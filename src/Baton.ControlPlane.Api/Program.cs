using Baton.Persistence;
using Baton.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddBatonTelemetry("control-plane-api");
builder.Services.AddBatonPersistence(builder.Configuration.GetConnectionString("Baton"));

// M1: services.AddWorkflows(typeof(...).Assembly) + startup validation/registration (WF-03, WF-04).
// M4: automations, triggers, Run now, webhooks, OIDC sign-in with Viewer/Operator/Admin roles.

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "control-plane-api" }));

app.Run();
