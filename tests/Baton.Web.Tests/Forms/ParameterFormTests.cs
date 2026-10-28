using System.Text.Json;
using Baton.Web.Forms;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Forms;

/// <summary>AUT-03: form generation from the schemas the SDK exports (design.md "Parameter form").</summary>
public sealed class ParameterFormTests
{
    // What WorkflowJson exports for: record P(int Count = 3, double Ratio = 0.5, bool Dry = false, Mode Mode = Mode.Fast,
    // DateTimeOffset? At = null, Inner? Nested = null, string? Note = null, int[]? Nums = null) with an enum and a nested record.
    private static readonly JsonElement Mixed = SampleData.Parse("""
        {"type":"object","properties":{
          "count":{"type":["string","integer"],"pattern":"^-?(?:0|[1-9]\\d*)$","default":3},
          "ratio":{"type":["string","number"],"default":0.5},
          "dry":{"type":"boolean","default":false},
          "mode":{"enum":["fast","slow"],"default":"fast"},
          "at":{"type":["string","null"],"format":"date-time","default":null},
          "nested":{"type":["object","null"],"properties":{"x":{"type":"integer"}},"default":null},
          "note":{"type":["string","null"],"default":null},
          "nums":{"type":["array","null"],"items":{"type":["string","integer"]},"default":null}}}
        """);

    [Fact]
    public void Each_exported_type_gets_its_input_kind()
    {
        var form = ParameterForm.FromSchema(Mixed);

        Assert.True(form.IsSupported);
        Assert.Equal(
            [FieldKind.WholeNumber, FieldKind.Number, FieldKind.Boolean, FieldKind.Choice, FieldKind.DateTime, FieldKind.Json, FieldKind.Text, FieldKind.Json],
            form.Fields.Select(f => f.Kind));
        Assert.All(form.Fields, f => Assert.False(f.Required));
        Assert.Equal("Count", form.Fields[0].Label);
    }

    [Fact]
    public void Required_means_listed_and_not_nullable()
    {
        var form = ParameterForm.FromSchema(SampleData.ParamsSchema);

        Assert.Equal([("region", true), ("reportDate", false), ("recipients", true)], form.Fields.Select(f => (f.Name, f.Required)));
        Assert.Equal([FieldKind.Text, FieldKind.Date, FieldKind.TextList], form.Fields.Select(f => f.Kind));
    }

    [Fact]
    public void Defaults_fill_the_inputs_and_build_back_to_typed_json()
    {
        var form = ParameterForm.FromSchema(Mixed);
        form.Load(null);

        Assert.Equal("3", form.Values["count"]);
        Assert.Equal("false", form.Values["dry"]);
        Assert.Equal("fast", form.Values["mode"]);

        form.Values["at"] = "2026-10-28T09:00:00+07:00";
        form.Values["nested"] = """{"x": 1}""";
        Assert.True(form.TryBuild(out var built, out var errors), string.Join(", ", errors));

        Assert.Equal(3, built["count"]!.GetValue<long>());
        Assert.Equal(0.5m, built["ratio"]!.GetValue<decimal>());
        Assert.False(built["dry"]!.GetValue<bool>());
        Assert.Equal("fast", built["mode"]!.GetValue<string>());
        Assert.Equal("2026-10-28T09:00:00.0000000+07:00", built["at"]!.GetValue<string>());
        Assert.Equal(1, built["nested"]!["x"]!.GetValue<int>());
        Assert.False(built.ContainsKey("note")); // empty and optional: the server applies the default
    }

    [Theory]
    [InlineData("count", "3.5", "Must be a whole number.")]
    [InlineData("ratio", "abc", "Must be a number.")]
    [InlineData("at", "2026-10-28T09:00:00", "Use ISO 8601 with an offset, e.g. 2026-10-28T09:00:00+07:00.")]
    [InlineData("nested", "{x", "Not valid JSON.")]
    public void Text_that_cannot_be_converted_names_its_field(string field, string text, string message)
    {
        var form = ParameterForm.FromSchema(Mixed);
        form.Load(null);
        form.Values[field] = text;

        Assert.False(form.TryBuild(out _, out var errors));
        Assert.Equal(message, errors[field]);
    }

    [Fact]
    public void Saved_parameters_are_loaded_into_the_inputs()
    {
        var form = ParameterForm.FromSchema(SampleData.ParamsSchema);

        form.Load(SampleData.Parse("""{"region":"EU","reportDate":"2026-10-01","recipients":["a@x","b@x"]}"""));

        Assert.Equal("EU", form.Values["region"]);
        Assert.Equal("2026-10-01", form.Values["reportDate"]);
        Assert.Equal("a@x\nb@x", form.Values["recipients"]);
    }

    [Fact]
    public void Schema_that_is_not_an_object_falls_back_to_a_json_editor()
    {
        var form = ParameterForm.FromSchema(SampleData.Parse("""{"type":"array","items":{"type":"string"}}"""));
        form.Load(null);
        form.RawJson = "[1]";

        Assert.False(form.IsSupported);
        Assert.False(form.TryBuild(out _, out var errors));
        Assert.Equal("Parameters must be a JSON object.", errors[""]);
    }

    [Theory]
    [InlineData("parameters.region", "region")]
    [InlineData("parameters.recipients[0]", "recipients")]
    [InlineData("parameters.nested.x", "nested")]
    [InlineData("name", null)]
    public void Api_error_keys_map_to_fields(string key, string? field) => Assert.Equal(field, ParameterForm.FieldOf(key));
}
