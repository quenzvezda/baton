using System.Net;
using Baton.Agent.Protocol.V1;
using Baton.Orchestrator.Agents;
using Baton.Orchestrator.Dispatch;
using Baton.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>
/// The orchestrator's gRPC <c>AgentService</c> on Kestrel (HTTP/2, free port) against the shared Postgres,
/// with the <c>LISTEN</c> wake-up for claims and, when asked, the dispatch loops.
/// </summary>
public sealed class OrchestratorHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;

    private OrchestratorHost(WebApplication app)
    {
        _app = app;
        Address = new Uri(app.Urls.First());
        _channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions { MaxReceiveMessageSize = ProtocolLimits.MaxMessageBytes });
        Client = new AgentService.AgentServiceClient(_channel);
    }

    public Uri Address { get; }

    public AgentService.AgentServiceClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="dispatch">Shortens dispatch timings for the test.</param>
    /// <param name="loops">Runs the reaper, retry promoter and limit reconciler too.</param>
    public static async Task<OrchestratorHost> StartAsync(PostgresFixture pg, Action<DispatchOptions>? dispatch = null, bool loops = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddBatonPersistence(pg.ConnectionString);
        builder.Services.AddBatonListenDataSource(pg.ConnectionString);
        builder.Services.AddAgentProtocol();
        if (dispatch is not null)
            builder.Services.Configure(dispatch);
        if (loops)
            builder.Services.AddDispatchLoops();

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
