using Baton.Abstractions;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Domain.Tests.Workflows;

/// <summary>WF-01, WF-03: AddWorkflows registers definitions and handlers; WorkflowSet checks they resolve.</summary>
public sealed class AddWorkflowsTests
{
    private static readonly Type[] SampleHandlers =
        [typeof(ExtractOrders), typeof(ExtractCustomers), typeof(BuildReport), typeof(PublishReport), typeof(NotifyOnFailure)];

    [Fact]
    public void Public_definitions_and_handlers_of_the_assembly_are_registered()
    {
        var services = new ServiceCollection().AddWorkflows(typeof(NightlySalesReport).Assembly);

        var definitions = services.Where(d => d.ServiceType == typeof(IWorkflowDefinition)).ToList();
        var definition = Assert.Single(definitions);
        Assert.Equal(typeof(NightlySalesReport), definition.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, definition.Lifetime);

        foreach (var handler in SampleHandlers)
            Assert.Contains(services, d => d.ServiceType == handler && d.Lifetime == ServiceLifetime.Scoped);

        Assert.Contains(services, d => d.ServiceType == typeof(WorkflowSet));
    }

    [Fact]
    public void Non_public_definitions_and_handlers_are_skipped()
    {
        var services = new ServiceCollection().AddWorkflows(typeof(NightlySalesReport).Assembly);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(StepA));
        Assert.DoesNotContain(services, d => d.ImplementationType?.IsGenericType == true);
    }

    [Fact]
    public void Registering_twice_does_not_duplicate()
    {
        var services = new ServiceCollection()
            .AddWorkflows(typeof(NightlySalesReport).Assembly)
            .AddWorkflows(typeof(NightlySalesReport).Assembly);

        Assert.Single(services, d => d.ServiceType == typeof(IWorkflowDefinition));
        Assert.Single(services, d => d.ServiceType == typeof(BuildReport));
    }

    [Fact]
    public void Workflow_set_compiles_registered_definitions_whose_handlers_resolve()
    {
        var services = new ServiceCollection().AddWorkflows(typeof(NightlySalesReport).Assembly);

        var set = new WorkflowSet([new NightlySalesReport()], new FromCollection(services));

        Assert.Equal("nightly-sales-report@2", Assert.Single(set.Workflows).Id);
    }

    [Fact]
    public void Handler_outside_the_scanned_assemblies_stops_startup()
    {
        // The definition is registered by hand; its handlers live in no scanned assembly.
        var services = new ServiceCollection().AddSingleton<IWorkflowDefinition, NightlySalesReport>();

        var ex = Assert.Throws<WorkflowDefinitionException>(
            () => new WorkflowSet([new NightlySalesReport()], new FromCollection(services)));

        Assert.Equal(SampleHandlers.Length, ex.Errors.Count);
        Assert.All(ex.Errors, e => Assert.Contains("is not registered in DI", e, StringComparison.Ordinal));
    }

    /// <summary>What the container answers for <see cref="IServiceProviderIsService"/>, without building one.</summary>
    private sealed class FromCollection(IServiceCollection services) : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => services.Any(d => d.ServiceType == serviceType);
    }
}
