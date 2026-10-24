using System.Text.Json.Nodes;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;

namespace Baton.Domain.Tests.Workflows;

/// <summary>AUT-03: the params JSON Schema is derived from the params type with the runtime serializer options.</summary>
public sealed class ParamsSchemaTests
{
    [Fact]
    public void Schema_of_the_spec_example_params_matches_the_snapshot()
    {
        var schema = WorkflowJson.TryExportParamsSchema(typeof(SalesReportParams), out var error);

        Assert.Null(error);
        Assert.Equal(Golden.Read("sales-report-params.schema.json"), schema);
    }

    [Fact]
    public void Schema_marks_a_required_string_and_an_optional_date()
    {
        var schema = JsonNode.Parse(WorkflowJson.TryExportParamsSchema(typeof(ReportParams), out _)!)!;

        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(["region"], required);
        Assert.Equal("string", schema["properties"]!["region"]!["type"]!.GetValue<string>());
        Assert.Equal("date", schema["properties"]!["reportDate"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Compiled_workflow_carries_the_schema()
    {
        var compiled = WorkflowCompiler.Compile(new NightlySalesReport());

        Assert.Equal(Golden.Read("sales-report-params.schema.json"), compiled.ParamsSchema);
    }

    private sealed record ReportParams(string Region, DateOnly? ReportDate = null);
}
