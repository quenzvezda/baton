using Baton.Agent.Worker;

var builder = Host.CreateApplicationBuilder(args);

// M2: register, heartbeat, drain and graceful shutdown. M3: claim loop and TaskExecutor (design spec §8 "Agent").
builder.Services.AddBatonAgent(builder.Configuration);

var host = builder.Build();
host.Run();
