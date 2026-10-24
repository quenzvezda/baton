using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Baton.Telemetry;

/// <summary>
/// OpenTelemetry baseline shared by the server processes (design spec §8): ASP.NET Core, HttpClient and
/// Npgsql traces and metrics, plus logs. Linked into each project as source. Instrumentation always
/// runs; export is off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (environment variable or
/// configuration), so local runs need no collector. Other <c>OTEL_*</c> settings apply as usual.
/// </summary>
internal static class BatonTelemetry
{
    public static TBuilder AddBatonTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = IsNotHealthProbe)
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsqlInstrumentation());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        return builder;
    }

    private static bool IsNotHealthProbe(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);
}
