using System.Net;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Tests.Samples;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Automations;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.EndToEnd;

/// <summary>
/// M4 task 4.1 and exit criterion: an automation created through the API with a cron trigger runs the design-spec
/// example workflow (<c>nightly-sales-report</c>) on two real agents to Succeeded — trigger loop, admission,
/// claims, DAG advance and finalization all live.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CronEndToEndTests(PostgresFixture pg)
{
    [Fact]
    public async Task Cron_automation_runs_the_example_workflow_on_two_agents_to_succeeded()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg, d =>
        {
            d.ClaimRecheck = TimeSpan.FromMilliseconds(500);
            d.AdmissionInterval = TimeSpan.FromMilliseconds(250);
        }, loops: true);
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var operatorClient = api.Client(BatonRoles.Operator);
        await DispatchScenario.RegisterAsync(pg, new NightlySalesReport());
        var (groupId, group) = await HostData.CreateGroupAsync(pg, "e2e");
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        await using var general = await AgentProcess.StartAsync(orchestrator.Address, group, token, slots: 2,
            services: s => s.AddSingleton<IReportRenderer, FakeRenderer>());
        await using var bigMemory = await AgentProcess.StartAsync(orchestrator.Address, group, token, slots: 2, labels: ["large-memory"],
            services: s => s.AddSingleton<IReportRenderer, FakeRenderer>());
        await Eventually.UntilAsync(() => HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM hosts WHERE host_group_id = @g", ("g", groupId)),
            count => count == 2, "both agents registered");

        var created = await operatorClient.PostAsJsonAsync("/api/automations", new AutomationRequest(
            HostData.NewName("APAC nightly sales report"), "nightly-sales-report", groupId,
            [new TriggerRequest(null, "cron", "* * * * *", "UTC")],
            Parameters: AutomationApi.ValidParameters));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var automation = (await created.Content.ReadFromJsonAsync<AutomationResponse>())!;

        try
        {
            // The next minute boundary, then the run.
            var runId = await Eventually.UntilAsync(() => HostData.ScalarAsync<Guid>(pg,
                    "SELECT COALESCE((SELECT id FROM workflow_runs WHERE automation_id = @a ORDER BY created_at LIMIT 1), '00000000-0000-0000-0000-000000000000')",
                    ("a", automation.Id)),
                id => id != Guid.Empty, "the cron trigger to fire", TimeSpan.FromSeconds(75));
            var status = await Eventually.UntilAsync(() => HostData.ScalarAsync<string>(pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)),
                s => s is "succeeded" or "failed" or "cancelled" or "timed_out", "the run to finish", TimeSpan.FromSeconds(60));

            Assert.Equal("succeeded", status);
            Assert.Equal("cron", await HostData.ScalarAsync<string>(pg, "SELECT trigger_kind FROM workflow_runs WHERE id = @id", ("id", runId)));
            var tasks = await HostData.ScalarAsync<string>(pg,
                "SELECT string_agg(task_key || '=' || status, ',' ORDER BY task_key) FROM task_runs WHERE workflow_run_id = @id", ("id", runId));
            Assert.Equal("build-report=succeeded,extract-customers=succeeded,extract-orders=succeeded,notify-failure=skipped,publish-report=succeeded", tasks);
            Assert.Equal(bigMemory.Name, await HostData.ScalarAsync<string>(pg, """
                SELECT h.name FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id JOIN hosts h ON h.id = a.host_id
                WHERE t.workflow_run_id = @id AND t.task_key = 'build-report' AND a.outcome = 'succeeded'
                """, ("id", runId)));
            Assert.Equal("https://reports.example.test/apac", await HostData.ScalarAsync<string>(pg,
                "SELECT output->>'location' FROM task_runs WHERE workflow_run_id = @id AND task_key = 'publish-report'", ("id", runId)));
            Assert.Equal(0, await HostData.ScalarAsync<int>(pg, "SELECT in_use FROM concurrency_limits WHERE key = @key",
                ("key", Baton.Domain.Automations.AutomationRules.AutomationLimitKey(automation.Id))));
        }
        finally
        {
            await operatorClient.DeleteAsync($"/api/automations/{automation.Id}");
        }
    }

    private sealed class FakeRenderer : IReportRenderer
    {
        public Task<Uri> RenderAsync(OrdersExtract orders, CustomersExtract customers, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(new Uri("https://reports.example.test/apac"));
    }
}
