var builder = Host.CreateApplicationBuilder(args);

// M2: AgentClient (gRPC Register/Heartbeat), AgentOptions bound from "Agent".
// M3: AgentWorker (claim loop + heartbeat loop) and TaskExecutor — design spec §8 "Agent".

var host = builder.Build();
host.Run();
