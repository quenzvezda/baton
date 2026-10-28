using Baton.Agent.Protocol.V1;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Loops;

namespace Baton.Orchestrator.Agents;

/// <summary>The agent-facing gRPC surface and dispatch, shared by <c>Program</c> and the integration tests.</summary>
public static class AgentProtocol
{
    public static IServiceCollection AddAgentProtocol(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<DispatchOptions>();
        if (configuration is not null)
            options.Bind(configuration.GetSection(DispatchOptions.Section));

        // Claim wake-ups; needs the keyed `listen` data source (AddBatonListenDataSource).
        services.AddSingleton<TasksReadySignal>();
        services.AddHostedService<TasksReadyListener>();
        services.AddGrpc(grpc =>
        {
            grpc.Interceptors.Add<AgentAuthInterceptor>();
            grpc.MaxReceiveMessageSize = ProtocolLimits.MaxMessageBytes;
            grpc.MaxSendMessageSize = ProtocolLimits.MaxMessageBytes;
        });
        return services;
    }

    /// <summary>The M3 dispatch and M4 run-lifecycle loops (design spec §8 "Orchestrator loops").</summary>
    public static IServiceCollection AddDispatchLoops(this IServiceCollection services)
    {
        services.AddHostedService<TriggerLoop>();
        services.AddHostedService<AdmissionLoop>();
        services.AddHostedService<ReaperLoop>();
        services.AddHostedService<RetryPromoterLoop>();
        services.AddHostedService<LimitReconcilerLoop>();
        return services;
    }

    public static IEndpointRouteBuilder MapAgentProtocol(this IEndpointRouteBuilder app)
    {
        app.MapGrpcService<AgentGrpcService>();
        return app;
    }
}
