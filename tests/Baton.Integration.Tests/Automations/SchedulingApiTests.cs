using System.Net;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Automations;
using Baton.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Baton.Integration.Tests.Automations;

/// <summary>AUT-08 trigger fields (M6 task 2.1), the fire-time preview (2.3) and placement mode (4.1) through the API.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SchedulingApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private AutomationApi _api = null!;

    public async Task InitializeAsync() => _api = await AutomationApi.StartAsync(pg);

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static TriggerRequest Cron(string? policy = null, int? limit = null) =>
        new(null, "cron", "0 2 * * *", "Asia/Singapore", MisfirePolicy: policy, CatchUpLimit: limit);

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Catch_up_limit_out_of_range_is_rejected(int limit)
    {
        var errors = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request(triggers: [Cron("catch_up", limit)])));

        Assert.Equal(["triggers[0].catchUpLimit"], errors.Keys);
    }

    [Fact]
    public async Task Catch_up_limit_on_another_policy_or_a_policy_on_a_webhook_is_rejected()
    {
        var onSkip = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request(triggers: [Cron("skip", 3)])));
        var unknown = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request(triggers: [Cron("sometimes")])));
        var onWebhook = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request(triggers: [new TriggerRequest(null, "webhook", MisfirePolicy: "skip")])));

        Assert.Equal(["triggers[0].catchUpLimit"], onSkip.Keys);
        Assert.Equal(["triggers[0].misfirePolicy"], unknown.Keys);
        Assert.Equal(["triggers[0].misfirePolicy"], onWebhook.Keys);
    }

    [Fact]
    public async Task Catch_up_without_limit_defaults_to_3()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request(triggers: [Cron("catch_up")]));

        var trigger = Assert.Single(automation.Triggers);
        Assert.Equal("catch_up", trigger.MisfirePolicy);
        Assert.Equal(3, trigger.CatchUpLimit);
    }

    [Fact]
    public async Task Misfire_policy_round_trips()
    {
        var (created, etag) = await _api.CreateAsync(_api.Request(triggers: [Cron(), Cron("catch_up", 7)]));
        Assert.All(created.Triggers.Where(t => t.CatchUpLimit is null), t => Assert.Equal("fire_once", t.MisfirePolicy));

        var catchUp = created.Triggers.Single(t => t.CatchUpLimit == 7);
        var fireOnce = created.Triggers.Single(t => t.CatchUpLimit is null);
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/automations/{created.Id}")
        {
            Content = JsonContent.Create(_api.Request(created.Name, [
                new TriggerRequest(catchUp.Id, "cron", "0 2 * * *", "Asia/Singapore", MisfirePolicy: "skip"),
                new TriggerRequest(fireOnce.Id, "cron", "0 2 * * *", "Asia/Singapore", MisfirePolicy: "catch_up", CatchUpLimit: 10),
            ])),
        };
        update.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));
        var response = await _api.Operator.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var read = (await _api.Operator.GetFromJsonAsync<AutomationResponse>($"/api/automations/{created.Id}"))!;
        Assert.Equal(("skip", (int?)null), read.Triggers.Where(t => t.Id == catchUp.Id).Select(t => (t.MisfirePolicy, t.CatchUpLimit)).Single());
        Assert.Equal(("catch_up", (int?)10), read.Triggers.Where(t => t.Id == fireOnce.Id).Select(t => (t.MisfirePolicy, t.CatchUpLimit)).Single());
    }

    [Fact]
    public async Task Preview_returns_the_next_5_fire_times_in_the_zone()
    {
        using var viewer = _api.Host.Client(Baton.ControlPlane.Api.Security.BatonRoles.Viewer);

        var times = (await viewer.GetFromJsonAsync<FireTimePreview[]>(
            "/api/cron/preview?expression=0%202%20*%20*%20*&timeZone=Asia/Singapore"))!;

        Assert.Equal(5, times.Length);
        Assert.All(times, t =>
        {
            Assert.EndsWith("T02:00:00", t.Local, StringComparison.Ordinal);
            Assert.Equal("+08:00", t.Offset);
            Assert.Equal(18, t.At.UtcDateTime.Hour); // 02:00 in Singapore is 18:00 UTC the day before
        });
        Assert.All(times.Zip(times.Skip(1)), p => Assert.Equal(TimeSpan.FromDays(1), p.Second.At - p.First.At));
        Assert.True(times[0].At > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Preview_of_an_expression_that_stops_firing_returns_fewer()
    {
        // 30 February never comes.
        var times = (await _api.Operator.GetFromJsonAsync<FireTimePreview[]>("/api/cron/preview?expression=0%200%2030%202%20*&timeZone=UTC"))!;

        Assert.True(times.Length < 5);
    }

    [Fact]
    public async Task Invalid_expression_preview_is_rejected_with_the_save_message()
    {
        var preview = await ValidationErrorsAsync(await _api.Operator.GetAsync("/api/cron/preview?expression=61%20*%20*%20*%20*&timeZone=UTC"));
        var save = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request(triggers: [new TriggerRequest(null, "cron", "61 * * * *", "UTC")])));

        Assert.Equal(["cronExpression"], preview.Keys);
        Assert.Equal(save["triggers[0].cronExpression"], preview["cronExpression"]);
    }

    [Fact]
    public async Task Default_placement_is_per_task()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request());

        Assert.Equal("per_task", automation.PlacementMode);
        var invalid = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request() with { PlacementMode = "same_host" }));
        Assert.Equal(["placementMode"], invalid.Keys);
    }

    [Fact]
    public async Task Placement_change_does_not_affect_active_runs()
    {
        var (automation, etag) = await _api.CreateAsync(_api.Request(policy: "allow", maxRuns: 10));
        var before = await _api.RunNowAsync(automation.Id);

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/automations/{automation.Id}")
        {
            Content = JsonContent.Create(_api.Request(automation.Name, [new TriggerRequest(automation.Triggers[0].Id, "cron", "0 2 * * *", "Asia/Singapore")],
                policy: "allow", maxRuns: 10) with { PlacementMode = "pinned_run" }),
        };
        update.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));
        var updated = (await (await _api.Operator.SendAsync(update)).Content.ReadFromJsonAsync<AutomationResponse>())!;
        var after = await _api.RunNowAsync(automation.Id);

        Assert.Equal("pinned_run", updated.PlacementMode);
        Assert.Equal("per_task", await PlacementAsync(before));
        Assert.Equal("pinned_run", await PlacementAsync(after));
    }

    [Fact]
    public async Task Clone_keeps_the_placement()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request() with { PlacementMode = "pinned_run" });

        var response = await _api.Operator.PostAsJsonAsync($"/api/automations/{automation.Id}/clone", new CloneRequest(HostData.NewName("copy")));

        Assert.Equal("pinned_run", (await response.Content.ReadFromJsonAsync<AutomationResponse>())!.PlacementMode);
    }

    private Task<string> PlacementAsync(Guid runId) =>
        HostData.ScalarAsync<string>(pg, "SELECT placement_mode FROM workflow_runs WHERE id = @id", ("id", runId));

    internal static async Task<IDictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())!.Errors;
    }
}
