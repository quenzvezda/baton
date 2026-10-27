using Baton.Agent.Execution;
using Baton.Agent.Protocol.V1;
using Baton.Agent.Worker;
using Baton.Domain.Workflows;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baton.Agent.Tests;

/// <summary>The agent's real services, wired to <see cref="FakeAgentService"/> instead of a gRPC channel.</summary>
public sealed class AgentHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public static AgentHarness Create(int slots = 2) => new(slots);

    private AgentHarness(int slots)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "test-host",
            ["Agent:HostGroup"] = "test",
            ["Agent:CapacitySlots"] = slots.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Agent:OrchestratorUrl"] = "http://127.0.0.1:1",
            ["Agent:Token"] = "baton_test",
            ["Agent:ClaimLongPollSeconds"] = "1",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBatonAgent(configuration, typeof(AgentTestWorkflow).Assembly);
        services.AddSingleton<AgentService.AgentServiceClient>(Fake);
        _services = services.BuildServiceProvider();
        Hash = _services.GetRequiredService<WorkflowSet>().Workflows.Single(w => w.Key == "agent-test").Hash;
    }

    public FakeAgentService Fake { get; } = new();

    public string Hash { get; }

    public AgentWorker Worker => _services.GetServices<IHostedService>().OfType<AgentWorker>().Single();

    public TaskExecutor Executor => _services.GetRequiredService<TaskExecutor>();

    public AgentClient Client => _services.GetRequiredService<AgentClient>();

    public TaskLease Lease(string taskKey, string value = "hello", TimeSpan? timeout = null, params (string Key, string Json)[] upstream) => new()
    {
        TaskRunId = Guid.NewGuid().ToString(),
        WorkflowRunId = Guid.NewGuid().ToString(),
        TaskKey = taskKey,
        DefinitionHash = Hash,
        Attempt = 1,
        LeaseToken = Random.Shared.NextInt64(1, long.MaxValue),
        Timeout = Duration.FromTimeSpan(timeout ?? TimeSpan.FromMinutes(1)),
        ParamsJson = ByteString.CopyFromUtf8($$"""{"value":"{{value}}"}"""),
        UpstreamOutputsJson = { upstream.ToDictionary(u => u.Key, u => ByteString.CopyFromUtf8(u.Json)) },
    };

    /// <summary>Waits until <paramref name="condition"/> holds, or fails the test after 10 s.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
