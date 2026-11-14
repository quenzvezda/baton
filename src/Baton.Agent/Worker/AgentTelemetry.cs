using Baton.Telemetry;
using Microsoft.Extensions.Hosting;

namespace Baton.Agent.Worker;

/// <summary>OBS-06 (M7 design.md decision 26): the shared baseline as service <c>agent</c>, instance = host name.</summary>
internal static class AgentTelemetry
{
    public const string ServiceName = "agent";

    public static TBuilder AddBatonAgentTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new AgentOptions();
        builder.Configuration.GetSection(AgentOptions.Section).Bind(options);
        return builder.AddBatonTelemetry(ServiceName, options.HostName);
    }
}
