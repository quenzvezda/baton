using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Runs;
using Baton.ControlPlane.Api.Security;
using Baton.ControlPlane.Api.Workflows;
using Baton.Persistence;
using Microsoft.AspNetCore.DataProtection;

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

        // Webhook secrets (ADR-0015): one key ring in Postgres for every replica, encrypted at rest with the
        // certificate named by Baton:DataProtection:CertificatePath when the deployment provides one.
        services.AddDataProtection().SetApplicationName("baton").PersistKeysToDbContext<BatonDbContext>()
            .ProtectKeysFromConfiguration(configuration);
        services.AddSingleton<WebhookSecrets>();
        return services;
    }

    public static IEndpointRouteBuilder MapControlPlane(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "control-plane-api" })).AllowAnonymous();
        app.MapMe();
        app.MapWorkflowCatalog();
        app.MapWorkflowLimits();
        app.MapHostGroups();
        app.MapHosts();
        app.MapAutomations();
        app.MapCronPreview();
        app.MapWebhooks();
        app.MapRuns();
        return app;
    }
}
