using Baton.Agent.Worker;
using Baton.Telemetry;

var builder = Host.CreateApplicationBuilder(args);

// M7 (OBS-06): traces, metrics and logs, with the agent's host name as the service instance.
builder.AddBatonAgentTelemetry();

// M2: register, heartbeat, drain and graceful shutdown. M3: claim loop and TaskExecutor (design spec §8 "Agent").
builder.Services.AddBatonAgent(builder.Configuration);

var host = builder.Build();
host.Run();
