using Baton.ControlPlane.Api;
using Baton.ControlPlane.Api.Workflows;
using Baton.Definitions;
using Baton.Domain.Workflows;
using Baton.Persistence;
using Baton.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.AddBatonTelemetry("control-plane-api");
builder.Services.AddBatonPersistence(builder.Configuration.GetConnectionString("Baton"));
builder.Services.AddWorkflows(typeof(DefinitionsAssembly).Assembly);
builder.Services.AddControlPlane(builder.Configuration);

var app = builder.Build();

// Refuses to start on an invalid definition or a changed graph under a registered key@version (WF-03, WF-04).
await app.Services.RegisterWorkflowsAsync();

app.UseAuthentication();
app.UseAuthorization();
app.MapControlPlane();

await app.RunAsync();
