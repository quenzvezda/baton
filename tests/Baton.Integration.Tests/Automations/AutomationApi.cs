using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Tests.Samples;
using Baton.Integration.Tests.Infrastructure;

namespace Baton.Integration.Tests.Automations;

/// <summary>
/// The API with an Operator client, one host group and the design-spec example workflow
/// (<c>nightly-sales-report@2</c>, params <c>region</c>, <c>reportDate</c>, <c>recipients</c>) registered.
/// </summary>
internal sealed class AutomationApi : IAsyncDisposable
{
    public static readonly JsonElement ValidParameters =
        JsonSerializer.SerializeToElement(new { region = "apac", reportDate = (string?)null, recipients = new[] { "ops@example.test" } });

    private AutomationApi(ControlPlaneHost host, Guid groupId, HttpClient operatorClient)
    {
        Host = host;
        GroupId = groupId;
        Operator = operatorClient;
    }

    public ControlPlaneHost Host { get; }
    public Guid GroupId { get; }
    public HttpClient Operator { get; }

    public static async Task<AutomationApi> StartAsync(PostgresFixture pg)
    {
        await DispatchScenario.RegisterAsync(pg, new NightlySalesReport());
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var host = await ControlPlaneHost.StartAsync(pg);
        return new AutomationApi(host, groupId, host.Client(BatonRoles.Operator));
    }

    public AutomationRequest Request(string? name = null, TriggerRequest[]? triggers = null, string policy = "skip", int maxRuns = 1) =>
        new(name ?? HostData.NewName("apac"), "nightly-sales-report", GroupId,
            triggers ?? [new TriggerRequest(null, "cron", "0 2 * * *", "Asia/Singapore")],
            Parameters: ValidParameters, ConcurrencyPolicy: policy, MaxConcurrentRuns: maxRuns);

    public async Task<(AutomationResponse Automation, string ETag)> CreateAsync(AutomationRequest request)
    {
        var response = await Operator.PostAsJsonAsync("/api/automations", request);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return ((await response.Content.ReadFromJsonAsync<AutomationResponse>())!, response.Headers.ETag!.Tag);
    }

    public async Task<Guid> RunNowAsync(Guid automationId)
    {
        var response = await Operator.PostAsync($"/api/automations/{automationId}/runs", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
    }

    public async ValueTask DisposeAsync()
    {
        Operator.Dispose();
        await Host.DisposeAsync();
    }
}
