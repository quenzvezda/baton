using System.Net;
using Baton.Agent.Protocol.V1;
using Baton.Orchestrator.Agents;
using Baton.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>The orchestrator's gRPC <c>AgentService</c> on Kestrel (HTTP/2, free port) against the shared Postgres.</summary>
public sealed class OrchestratorHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;

    private OrchestratorHost(WebApplication app)
    {
        _app = app;
        _channel = GrpcChannel.ForAddress(app.Urls.First());
        Client = new AgentService.AgentServiceClient(_channel);
    }

    public AgentService.AgentServiceClient Client { get; }

    public static async Task<OrchestratorHost> StartAsync(PostgresFixture pg)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddBatonPersistence(pg.ConnectionString);
        builder.Services.AddAgentProtocol();

        var app = builder.Build();
        app.MapAgentProtocol();
        await app.StartAsync();
        return new OrchestratorHost(app);
    }

    public static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    public async Task<RegisterReply> RegisterAsync(string token, string hostName, string group, int slots = 4,
        string version = "1.0.0", string[]? labels = null, string[]? hashes = null) =>
        await Client.RegisterAsync(
            new RegisterRequest
            {
                HostName = hostName,
                HostGroup = group,
                CapacitySlots = slots,
                AgentVersion = version,
                Labels = { labels ?? [] },
                DefinitionHashes = { hashes ?? [] },
            },
            Bearer(token));

    public async Task<HeartbeatReply> HeartbeatAsync(string token, string hostId, bool draining = false) =>
        await Client.HeartbeatAsync(new HeartbeatRequest { HostId = hostId, Draining = draining }, Bearer(token));

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _app.DisposeAsync();
    }
}
