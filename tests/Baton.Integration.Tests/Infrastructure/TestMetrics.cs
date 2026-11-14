using System.Diagnostics.Metrics;
using Baton.Orchestrator.Health;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary><see cref="OrchestratorMetrics"/> on a private meter factory, so tests can listen to their own meters only.</summary>
public static class TestMetrics
{
    public static OrchestratorMetrics Create() => Create(out _);

    public static OrchestratorMetrics Create(out IMeterFactory factory)
    {
        factory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return new OrchestratorMetrics(factory);
    }
}
