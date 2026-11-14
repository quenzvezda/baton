using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Baton.Telemetry;

/// <summary>Npgsql traces and metrics for the processes that use Postgres (API, orchestrator).</summary>
internal static class BatonNpgsqlTelemetry
{
    public static TBuilder AddBatonNpgsqlTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddNpgsql())
            .WithMetrics(metrics => metrics.AddNpgsqlInstrumentation());
        return builder;
    }
}
