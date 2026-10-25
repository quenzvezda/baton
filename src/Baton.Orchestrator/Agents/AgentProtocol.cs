namespace Baton.Orchestrator.Agents;

/// <summary>The agent-facing gRPC surface, shared by <c>Program</c> and the integration tests.</summary>
public static class AgentProtocol
{
    public static IServiceCollection AddAgentProtocol(this IServiceCollection services)
    {
        services.AddGrpc(options => options.Interceptors.Add<AgentAuthInterceptor>());
        return services;
    }

    public static IEndpointRouteBuilder MapAgentProtocol(this IEndpointRouteBuilder app)
    {
        app.MapGrpcService<AgentGrpcService>();
        return app;
    }
}
