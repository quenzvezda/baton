using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Baton.Telemetry;

/// <summary>
/// OpenTelemetry baseline shared by every Baton process (design spec §8, OBS-06): HttpClient traces and metrics,
/// Baton's own activity sources and meters (<c>Baton.*</c>), plus logs. Linked into each project as source;
/// <c>BatonAspNetCoreTelemetry</c> and <c>BatonNpgsqlTelemetry</c> add what web hosts and database users need.
/// Instrumentation always runs; export is off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (environment
/// variable or configuration), so local runs need no collector. Other <c>OTEL_*</c> settings apply as usual.
/// The resource names the service, its version and its instance (M7 design.md decision 26).
/// </summary>
internal static class BatonTelemetry
{
    public const string SourcePrefix = "Baton.*";

    /// <param name="instanceId">Defaults to the machine name; agents pass their host name.</param>
    public static TBuilder AddBatonTelemetry<TBuilder>(this TBuilder builder, string serviceName, string? instanceId = null)
        where TBuilder : IHostApplicationBuilder
    {
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName,
                serviceVersion: ServiceVersion(),
                autoGenerateServiceInstanceId: false,
                serviceInstanceId: instanceId ?? Environment.MachineName.ToLowerInvariant()))
            .WithTracing(tracing => tracing
                .AddSource(SourcePrefix)
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(SourcePrefix)
                .AddHttpClientInstrumentation());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        return builder;
    }

    private static string ServiceVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BatonTelemetry).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
