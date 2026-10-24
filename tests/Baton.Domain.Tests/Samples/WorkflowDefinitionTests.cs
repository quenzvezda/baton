using Baton.Abstractions;

namespace Baton.Domain.Tests.Samples;

public sealed class WorkflowDefinitionTests
{
    [Fact]
    public void Spec_example_exposes_key_version_and_params_type()
    {
        var definition = new NightlySalesReport();

        Assert.Equal("nightly-sales-report", definition.Key);
        Assert.Equal(2, definition.Version);
        Assert.Equal(typeof(SalesReportParams), definition.ParamsType);
    }

    [Fact]
    public void Display_name_defaults_to_the_key_in_words()
    {
        Assert.Equal("Nightly sales report", new NightlySalesReport().DisplayName);
    }

    [Fact]
    public void Display_name_can_be_overridden()
    {
        Assert.Equal("Sales report (nightly)", new NamedReport().DisplayName);
    }

    [Fact]
    public void Build_rejects_a_null_builder()
    {
        Assert.Throws<ArgumentNullException>(() => new NightlySalesReport().Build(null!));
    }

    private sealed class NamedReport : WorkflowDefinition<SalesReportParams>
    {
        public override string Key => "nightly-sales-report";
        public override int Version => 1;
        public override string DisplayName => "Sales report (nightly)";

        protected override void Define(IWorkflowBuilder<SalesReportParams> wf) =>
            wf.Task<ExtractOrders>("extract-orders");
    }
}
