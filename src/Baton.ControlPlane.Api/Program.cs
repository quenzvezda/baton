var builder = WebApplication.CreateBuilder(args);

// M1: services.AddWorkflows(typeof(...).Assembly) + startup validation/registration (WF-03, WF-04).
// M4: automations, triggers, Run now, webhooks, OIDC sign-in with Viewer/Operator/Admin roles.

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "control-plane-api" }));

app.Run();
