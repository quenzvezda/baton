using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Baton.Telemetry;

/// <summary>ASP.NET Core traces and metrics for the web hosts (API, orchestrator, console), health probes left out.</summary>
internal static class BatonAspNetCoreTelemetry
{
    public static TBuilder AddBatonAspNetCoreTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(o => o.Filter = IsNotHealthProbe))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());
        return builder;
    }

    private static bool IsNotHealthProbe(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);
}
