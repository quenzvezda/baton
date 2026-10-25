using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.ControlPlane.Api.Workflows;

namespace Baton.ControlPlane.Api;

/// <summary>
/// Services and endpoints of the Control Plane API, shared by <c>Program</c> and the API tests so both
/// serve the same surface. Every endpoint declares a role policy or is explicitly anonymous (ADR-0014).
/// </summary>
public static class ControlPlane
{
    public static IServiceCollection AddControlPlane(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddBatonSecurity(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapControlPlane(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "control-plane-api" })).AllowAnonymous();
        app.MapWorkflowCatalog();
        app.MapHostGroups();
        app.MapHosts();
        return app;
    }
}
