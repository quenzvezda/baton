using Baton.Abstractions;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;

namespace Baton.Domain.Tests.Workflows;

/// <summary>WF-04: canonical graph JSON and its hash are deterministic and locked.</summary>
public sealed class CanonicalGraphTests
{
    [Fact]
    public void Canonical_json_matches_the_golden_file()
    {
        var expected = Golden.Read("nightly-sales-report.v2.json");

        var json = CanonicalGraph.ToJson(WorkflowGraph.FromDefinition(new NightlySalesReport()));

        // A diff here changes the hash of every registered definition: only accept it with a migration plan.
        Assert.Equal(expected, json);
    }

    [Fact]
    public void Same_graph_declared_in_a_different_order_has_the_same_hash()
    {
        var original = Hash(new NightlySalesReport());

        var reordered = Hash(new InlineWorkflow<SalesReportParams>(wf =>
        {
            var customers = wf.Task<ExtractCustomers>("extract-customers").Retry(3, TimeSpan.FromSeconds(30));
            var orders = wf.Task<ExtractOrders>("extract-orders")
                .Timeout(TimeSpan.FromMinutes(15))
                .Retry(maxAttempts: 4, initialDelay: TimeSpan.FromSeconds(30));
            var report = wf.Task<BuildReport>("build-report")
                .RequireLabels("large-memory", "large-memory")
                .DependsOn(customers)
                .DependsOn(orders);
            wf.Task<NotifyOnFailure>("notify-failure").When(TriggerRule.OneFailed).DependsOn(report, orders, customers);
            wf.Task<PublishReport>("publish-report").DependsOn(report);
        }, key: "nightly-sales-report", version: 2));

        Assert.Equal(original, reordered);
    }

    [Fact]
    public void Changing_an_option_changes_the_hash()
    {
        var original = Hash(new NightlySalesReport());

        var changed = Hash(new InlineWorkflow<SalesReportParams>(wf =>
        {
            var orders = wf.Task<ExtractOrders>("extract-orders").Retry(4, TimeSpan.FromSeconds(30)).Timeout(TimeSpan.FromMinutes(16));
            var customers = wf.Task<ExtractCustomers>("extract-customers");
            var report = wf.Task<BuildReport>("build-report").DependsOn(orders, customers).RequireLabels("large-memory");
            wf.Task<PublishReport>("publish-report").DependsOn(report);
            wf.Task<NotifyOnFailure>("notify-failure").DependsOn(orders, customers, report).When(TriggerRule.OneFailed);
        }, key: "nightly-sales-report", version: 2));

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void Display_name_is_not_part_of_the_hash()
    {
        var plain = Hash(InlineWorkflow.Of(wf => wf.Task<StepA>("a")));
        var renamed = Hash(new InlineWorkflow<NoParams>(wf => wf.Task<StepA>("a"), displayName: "Something else"));

        Assert.Equal(plain, renamed);
    }

    [Fact]
    public void Version_is_part_of_the_hash()
    {
        var v1 = Hash(new InlineWorkflow<NoParams>(wf => wf.Task<StepA>("a"), version: 1));
        var v2 = Hash(new InlineWorkflow<NoParams>(wf => wf.Task<StepA>("a"), version: 2));

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void Hash_is_lowercase_hex_sha256_of_the_canonical_json()
    {
        var hash = CanonicalGraph.Hash("{}");

        Assert.Equal("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", hash);
    }

    private static string Hash(IWorkflowDefinition definition) =>
        CanonicalGraph.Hash(CanonicalGraph.ToJson(WorkflowGraph.FromDefinition(definition)));
}

internal static class Golden
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Workflows", "Golden", name)).TrimEnd('\r', '\n');
}
