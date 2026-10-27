using System.Reflection;
using Baton.Agent.Execution;
using Baton.Agent.Protocol.V1;
using Baton.Definitions;
using Baton.Domain.Hosts;
using Baton.Domain.Workflows;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Baton.Agent.Worker;

public static class AgentServiceCollectionExtensions
{
    /// <summary>Options, gRPC channel and client with retries, lifecycle, executor and the worker (design.md "Agent resilience").</summary>
    /// <param name="definitionAssemblies">Where the definitions and handlers live; the Baton.Definitions package when none are given.</param>
    public static IServiceCollection AddBatonAgent(
        this IServiceCollection services, IConfiguration configuration, params Assembly[] definitionAssemblies)
    {
        services.AddOptions<AgentOptions>()
            .Bind(configuration.GetSection(AgentOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();

        services.AddResiliencePipeline(AgentClient.TransientPipeline, pipeline => pipeline.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<RpcException>(AgentClient.IsTransient),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(10),
            MaxRetryAttempts = 4,
        }));

        services.AddSingleton(sp => GrpcChannel.ForAddress(
            sp.GetRequiredService<IOptions<AgentOptions>>().Value.OrchestratorUrl!,
            new GrpcChannelOptions
            {
                MaxReceiveMessageSize = ProtocolLimits.MaxMessageBytes,
                MaxSendMessageSize = ProtocolLimits.MaxMessageBytes,
            }));
        services.AddSingleton(sp => new AgentService.AgentServiceClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<AgentClient>();

        // The definitions this build carries; their hashes are reported at registration (HST-02).
        services.AddWorkflows(definitionAssemblies.Length > 0 ? definitionAssemblies : [typeof(DefinitionsAssembly).Assembly]);
        services.AddSingleton<AgentIdentity>();
        services.AddSingleton<AgentLifecycle>();
        services.AddSingleton<TaskExecutor>();
        services.AddHostedService<AgentWorker>();
        return services;
    }

    private sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
    {
        public ValidateOptionsResult Validate(string? name, AgentOptions options)
        {
            var problems = options.Problems().ToList();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}
