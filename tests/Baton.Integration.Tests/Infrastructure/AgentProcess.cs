using Baton.Agent.Protocol.V1;
using Baton.Agent.Worker;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>
/// A real agent (<c>AddBatonAgent</c>, <c>AgentWorker</c>, <c>TaskExecutor</c>) in-process, carrying this test
/// assembly's public definitions. <see cref="Kill"/> cuts its network: it can no longer heartbeat or report,
/// which is what a dead process looks like to the orchestrator (M3 design.md decision 9).
/// </summary>
public sealed class AgentProcess : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly KillSwitch _network;

    private AgentProcess(IHost host, KillSwitch network, string name)
    {
        _host = host;
        _network = network;
        Name = name;
    }

    public string Name { get; }

    /// <param name="labels">The host's labels (HST-02).</param>
    /// <param name="services">Extra services for the handlers, e.g. the example workflow's renderer.</param>
    /// <param name="failover">
    /// Further orchestrator replicas, tried in turn when a call to the current one cannot connect — what a load
    /// balancer in front of the replicas does (G1 chaos test).
    /// </param>
    public static async Task<AgentProcess> StartAsync(
        Uri orchestrator, string group, string token, int slots = 2, string[]? labels = null, Action<IServiceCollection>? services = null,
        IReadOnlyList<Uri>? failover = null)
    {
        var name = HostData.NewName("agent");
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = name,
            ["Agent:HostGroup"] = group,
            ["Agent:CapacitySlots"] = slots.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Agent:OrchestratorUrl"] = orchestrator.ToString(),
            ["Agent:Token"] = token,
            ["Agent:ClaimLongPollSeconds"] = "2",
        });
        builder.Configuration.AddInMemoryCollection((labels ?? []).Select((label, i) =>
            KeyValuePair.Create($"Agent:Labels:{i}", (string?)label)));
        builder.Services.AddBatonAgent(builder.Configuration, typeof(AgentProcess).Assembly);
        services?.Invoke(builder.Services);

        HttpMessageHandler transport = new SocketsHttpHandler();
        if (failover is { Count: > 0 })
        {
            // Not a SocketsHttpHandler: given one, Grpc.Net.Client opens its own subchannel connections straight to the
            // channel's address and the rewritten request URI would be ignored.
            transport = new FailoverHandler(new HttpClientHandler(), [orchestrator, .. failover]);
        }
        var network = new KillSwitch(transport);
        builder.Services.AddSingleton(GrpcChannel.ForAddress(orchestrator, new GrpcChannelOptions
        {
            HttpHandler = network,
            MaxReceiveMessageSize = ProtocolLimits.MaxMessageBytes,
        }));

        var host = builder.Build();
        await host.StartAsync();
        return new AgentProcess(host, network, name);
    }

    /// <summary>From now on every call fails and every call in flight is aborted.</summary>
    public void Kill() => _network.Kill();

    /// <summary>A graceful shutdown (rolling upgrade): drains, waits up to <paramref name="timeout"/> for running tasks.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _host.StopAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _host.StopAsync(timeout.Token);
        _host.Dispose();
        _network.Dispose();
    }

    /// <summary>Sends to the current replica; a connection failure moves to the next one for later calls.</summary>
    private sealed class FailoverHandler(HttpMessageHandler inner, IReadOnlyList<Uri> replicas) : DelegatingHandler(inner)
    {
        private int _current;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Volatile.Read(ref _current);
            request.RequestUri = new Uri(replicas[index % replicas.Count], request.RequestUri!.PathAndQuery);
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                Interlocked.CompareExchange(ref _current, index + 1, index);
                throw;
            }
        }
    }

    private sealed class KillSwitch(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CancellationTokenSource _killed = new();

        public void Kill() => _killed.Cancel();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_killed.IsCancellationRequested)
                throw new HttpRequestException("The agent's network is cut.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _killed.Token);
            return await base.SendAsync(request, linked.Token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _killed.Dispose();
            base.Dispose(disposing);
        }
    }
}
