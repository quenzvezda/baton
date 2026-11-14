using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Baton.Agent.Execution;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Tests.Samples;
using Baton.Integration.Tests.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Runs;
using Baton.Persistence;
using Baton.Persistence.Queue;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.EndToEnd;

/// <summary>OBS-06 "Trace per run": the run's trace starts where it is created and its handler spans join it (M7 tasks 6.1–6.2).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RunTraceTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public RunTraceTests(PostgresFixture pg)
    {
        _pg = pg;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or BatonActivities.OrchestratorSourceName or TaskExecutor.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Run_now_run_carries_the_request_trace()
    {
        await using var api = await AutomationApi.StartAsync(_pg);
        var (automation, _) = await api.CreateAsync(api.Request(policy: "allow", maxRuns: 5));
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/automations/{automation.Id}/runs");
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        var response = await api.Operator.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
        Assert.Equal(traceId, TraceIdOf(await TraceParentAsync(runId)));
    }

    [Fact]
    public async Task Cron_run_carries_the_trigger_trace()
    {
        var s = await RunScenario.CreateAsync(_pg, policy: "allow", maxRuns: 10);
        await TriggerLoopTests.AddCronTriggerAsync(_pg, s.AutomationId, "0 2 * * *", "UTC", dueSecondsAgo: 1);

        await new CronTriggers(_pg.DataSource).FireDueAsync(CancellationToken.None);

        var runId = await HostData.ScalarAsync<Guid>(_pg, "SELECT id FROM workflow_runs WHERE automation_id = @id", ("id", s.AutomationId));
        var fire = Assert.Single(_stopped, a => a.OperationName == BatonActivities.TriggerFire
                                                && (string?)a.GetTagItem("baton.automation.id") == s.AutomationId.ToString());
        Assert.Equal(default, fire.ParentSpanId);
        var traceParent = await TraceParentAsync(runId);
        Assert.Equal(fire.TraceId, TraceIdOf(traceParent));
        Assert.Equal($"00-{fire.TraceId}-{fire.SpanId}-01", traceParent);
    }

    [Fact]
    public async Task Two_tasks_on_two_agents_share_one_trace()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(_pg, d =>
        {
            d.ClaimRecheck = TimeSpan.FromMilliseconds(500);
            d.AdmissionInterval = TimeSpan.FromMilliseconds(250);
        }, loops: true);
        await using var api = await ControlPlaneHost.StartAsync(_pg);
        using var operatorClient = api.Client(BatonRoles.Operator);
        await DispatchScenario.RegisterAsync(_pg, new NightlySalesReport());
        var (groupId, group) = await HostData.CreateGroupAsync(_pg, "trace");
        var (_, token) = await HostData.IssueCredentialAsync(_pg, groupId);
        await using var general = await AgentProcess.StartAsync(orchestrator.Address, group, token, slots: 2,
            services: s => s.AddSingleton<IReportRenderer, FakeRenderer>());
        await using var bigMemory = await AgentProcess.StartAsync(orchestrator.Address, group, token, slots: 2, labels: ["large-memory"],
            services: s => s.AddSingleton<IReportRenderer, FakeRenderer>());
        var created = await operatorClient.PostAsJsonAsync("/api/automations", new AutomationRequest(
            HostData.NewName("traced report"), "nightly-sales-report", groupId, [new TriggerRequest(null, "manual")],
            Parameters: AutomationApi.ValidParameters));
        var automation = (await created.Content.ReadFromJsonAsync<AutomationResponse>())!;

        try
        {
            var traceId = ActivityTraceId.CreateRandom();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/automations/{automation.Id}/runs");
            request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
            var runId = (await (await operatorClient.SendAsync(request)).Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
            var status = await Eventually.UntilAsync(() => HostData.ScalarAsync<string>(_pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)),
                s => s is "succeeded" or "failed" or "cancelled" or "timed_out", "the run to finish", TimeSpan.FromSeconds(60));
            Assert.Equal("succeeded", status);

            var spans = _stopped.Where(a => a.OperationName == TaskExecutor.ExecuteActivity
                                            && (string?)a.GetTagItem("baton.run.id") == runId.ToString()).ToList();
            Assert.Equal(["build-report", "extract-customers", "extract-orders", "publish-report"],
                spans.Select(s => (string)s.GetTagItem("baton.task.key")!).Order(StringComparer.Ordinal));
            Assert.All(spans, span => Assert.Equal(traceId, span.TraceId));
            Assert.Equal(bigMemory.Name, spans.Single(s => (string?)s.GetTagItem("baton.task.key") == "build-report").GetTagItem("baton.host"));
        }
        finally
        {
            await operatorClient.DeleteAsync($"/api/automations/{automation.Id}");
        }
    }

    private Task<string> TraceParentAsync(Guid runId) =>
        HostData.ScalarAsync<string>(_pg, "SELECT trace_parent FROM workflow_runs WHERE id = @id", ("id", runId));

    private static ActivityTraceId TraceIdOf(string traceParent) => ActivityContext.Parse(traceParent, null).TraceId;

    private sealed class FakeRenderer : IReportRenderer
    {
        public Task<Uri> RenderAsync(OrdersExtract orders, CustomersExtract customers, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(new Uri("https://reports.example.test/apac"));
    }
}
