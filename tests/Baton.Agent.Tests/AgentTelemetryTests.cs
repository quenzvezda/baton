using Baton.Agent.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Baton.Agent.Tests;

/// <summary>OBS-06 "Telemetry export": the agent exports like the other processes and needs no collector.</summary>
public sealed class AgentTelemetryTests
{
    [Fact]
    public async Task Agent_starts_with_telemetry_and_no_endpoint()
    {
        using var host = Build(new() { ["Agent:Name"] = "etl-07" });

        await host.StartAsync();
        await host.StopAsync();

        Assert.NotNull(host.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void Resource_names_service_version_and_instance()
    {
        using var host = Build(new() { ["Agent:Name"] = "etl-07" });

        var attributes = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("agent", attributes["service.name"]);
        Assert.Equal("etl-07", attributes["service.instance.id"]);
        Assert.False(string.IsNullOrEmpty(attributes["service.version"] as string));
    }

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddBatonAgentTelemetry();
        return builder.Build();
    }
}
