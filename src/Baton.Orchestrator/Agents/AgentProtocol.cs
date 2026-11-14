using Baton.Agent.Protocol.V1;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Domain.Runs;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Baton.Orchestrator.Agents;

/// <summary>The agent-facing gRPC surface and dispatch, shared by <c>Program</c> and the integration tests.</summary>
public static class AgentProtocol
{
    public static IServiceCollection AddAgentProtocol(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<DispatchOptions>();
        if (configuration is not null)
            options.Bind(configuration.GetSection(DispatchOptions.Section));
        options.Validate(o => o.Aging.Validate().Count == 0,
                $"{DispatchOptions.Section}:{nameof(DispatchOptions.AgingStep)} must be at least {AgingSettings.MinStep.TotalSeconds:0} s "
                + $"and {DispatchOptions.Section}:{nameof(DispatchOptions.AgingCap)} between 0 and {TaskPriority.Max}.")
            .ValidateOnStart();

        // Claim wake-ups; needs the keyed `listen` data source (AddBatonListenDataSource).
        services.AddSingleton<TasksReadySignal>();
        services.AddHostedService<TasksReadyListener>();
        services.AddGrpc(grpc =>
        {
            grpc.Interceptors.Add<DatabaseUnavailableInterceptor>(); // outermost: also covers the auth lookup
            grpc.Interceptors.Add<AgentAuthInterceptor>();
            grpc.MaxReceiveMessageSize = ProtocolLimits.MaxMessageBytes;
            grpc.MaxSendMessageSize = ProtocolLimits.MaxMessageBytes;
        });
        return services;
    }

    /// <summary>The M3 dispatch and M4 run-lifecycle loops (design spec §8 "Orchestrator loops").</summary>
    public static IServiceCollection AddDispatchLoops(this IServiceCollection services)
    {
        services.AddOrchestratorMetrics();
        services.AddHostedService<TriggerLoop>();
        services.AddHostedService<AdmissionLoop>();
        services.AddHostedService<ReaperLoop>();
        services.AddHostedService<RetryPromoterLoop>();
        services.AddHostedService<LimitReconcilerLoop>();
        services.AddHostedService<AgingLoop>();
        return services;
    }

    /// <summary>The orchestrator's counters (<see cref="OrchestratorMetrics"/>); safe to call more than once.</summary>
    public static IServiceCollection AddOrchestratorMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton<OrchestratorMetrics>();
        return services;
    }

    public static IEndpointRouteBuilder MapAgentProtocol(this IEndpointRouteBuilder app)
    {
        app.MapGrpcService<AgentGrpcService>();
        return app;
    }
}
