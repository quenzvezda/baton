using System.Text.Json;
using Baton.Web.Api;
using Baton.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Web.Tests.Infrastructure;

/// <summary>A bUnit context with the console's services over a <see cref="FakeBatonApi"/>, signed in with a role.</summary>
internal sealed class ConsoleContext : BunitContext
{
    public ConsoleContext(string role = Roles.Viewer)
    {
        Api.User = new CurrentUser($"Test {role}", role switch
        {
            Roles.Admin => [Roles.Viewer, Roles.Operator, Roles.Admin],
            Roles.Operator => [Roles.Viewer, Roles.Operator],
            _ => [Roles.Viewer],
        });
        Services.AddSingleton<IBatonApi>(Api);
        Services.AddScoped<UserSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public FakeBatonApi Api { get; } = new();
}

/// <summary>The design spec's example workflow (§5) as the API returns it.</summary>
internal static class SampleData
{
    public const string WorkflowKey = "nightly-sales-report";

    public static readonly JsonElement Graph = Parse("""
        {"key":"nightly-sales-report","version":2,"params":"SalesReportParams","tasks":[
          {"key":"build-report","dependsOn":["extract-customers","extract-orders"]},
          {"key":"extract-customers","dependsOn":[]},
          {"key":"extract-orders","dependsOn":[]},
          {"key":"notify-failure","dependsOn":["build-report","extract-customers","extract-orders"]},
          {"key":"publish-report","dependsOn":["build-report"]}]}
        """);

    public static readonly JsonElement GraphV1 = Parse("""
        {"key":"nightly-sales-report","version":1,"params":"SalesReportParams","tasks":[{"key":"build-report","dependsOn":[]}]}
        """);

    public static readonly JsonElement ParamsSchema = Parse("""
        {"type":"object","properties":{"region":{"type":"string"},"reportDate":{"type":["string","null"],"format":"date"},
         "recipients":{"type":"array","items":{"type":"string"}}},"required":["region","reportDate","recipients"]}
        """);

    public static readonly HostGroup Group = new(Guid.CreateVersion7(), "default", null, 8, 60, false);

    public static void AddWorkflow(FakeBatonApi api)
    {
        var registered = DateTimeOffset.UtcNow.AddDays(-1);
        api.Workflows.Add(new WorkflowSummary(WorkflowKey, 2, "Nightly sales report", false, registered));
        api.WorkflowVersions.Add(new WorkflowVersionDetail(WorkflowKey, 2, "Nightly sales report", new string('a', 64), Graph, ParamsSchema,
            false, registered, [1, 2]));
        api.WorkflowVersions.Add(new WorkflowVersionDetail(WorkflowKey, 1, "Nightly sales report", new string('b', 64), GraphV1, ParamsSchema,
            false, registered.AddDays(-30), [1, 2]));
        api.HostGroups.Add(Group);
    }

    public static RunDetail FailedRun(Guid id)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        TaskRun Task(string key, string status, int attempt = 1, JsonElement? error = null, IReadOnlyList<TaskAttempt>? attempts = null) =>
            new(Guid.CreateVersion7(), key, status, attempt, 3, at, attempt > 0 ? at : null, attempt > 0 ? at.AddMinutes(1) : null,
                status == "succeeded" ? Parse("{\"count\":12}") : null, error, attempts ?? []);

        var hostId = Guid.CreateVersion7();
        var boom = Parse("""{"type":"TimeoutException","message":"warehouse did not answer"}""");
        return new RunDetail(
            id, Guid.CreateVersion7(), "Nightly EU", WorkflowKey, 2, new string('a', 64), Graph, "failed", 50, Group.Id, Group.Name, "cron",
            Parse("""{"region":"EU"}"""), null, null, at, at, at.AddMinutes(5), null,
            [
                Task("extract-orders", "succeeded", attempts: [new TaskAttempt(1, hostId, "agent-1", at, at.AddMinutes(1), "succeeded", null)]),
                Task("extract-customers", "failed", 2, boom,
                [
                    new TaskAttempt(1, hostId, "agent-1", at, at.AddMinutes(1), "failed", Parse("""{"type":"IOException","message":"reset"}""")),
                    new TaskAttempt(2, hostId, "agent-2", at.AddMinutes(2), at.AddMinutes(3), "failed", boom),
                ]),
                Task("build-report", "skipped", 0),
                Task("publish-report", "skipped", 0),
                Task("notify-failure", "succeeded", attempts: [new TaskAttempt(1, hostId, "agent-1", at, at.AddMinutes(1), "succeeded", null)]),
            ]);
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
