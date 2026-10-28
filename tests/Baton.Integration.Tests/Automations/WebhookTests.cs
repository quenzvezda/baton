using System.Net;
using System.Net.Http.Json;
using System.Text;
using Baton.ControlPlane.Api.Automations;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;

namespace Baton.Integration.Tests.Automations;

/// <summary>AUT-04 webhooks: signature, encrypted secrets and delivery dedupe (M4 task 2.5, ADR-0015).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class WebhookTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Body = """{"event":"orders.closed"}""";

    private AutomationApi _api = null!;
    private HttpClient _sender = null!;

    public async Task InitializeAsync()
    {
        _api = await AutomationApi.StartAsync(pg);
        _sender = _api.Host.Client(); // anonymous: the signature is the authentication
    }

    public async Task DisposeAsync()
    {
        _sender.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Correctly_signed_delivery_creates_one_run_and_answers_202_with_its_id()
    {
        var (automation, trigger, secret) = await CreateAsync();

        var response = await SendAsync(trigger.Id, WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body)), "delivery-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
        Assert.Equal("webhook", await HostData.ScalarAsync<string>(pg, "SELECT trigger_kind FROM workflow_runs WHERE id = @id", ("id", runId)));
        Assert.Equal(1L, await RunCountAsync(automation.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not a signature")]
    public async Task Missing_or_wrong_signature_is_401_and_creates_no_run(string? signature)
    {
        var (automation, trigger, _) = await CreateAsync();

        var response = await SendAsync(trigger.Id, signature, "delivery-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Signature_made_with_another_secret_or_for_an_unknown_trigger_is_401()
    {
        var (_, trigger, secret) = await CreateAsync();
        var signature = WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(trigger.Id, WebhookSignature.Compute("whsec_other", Encoding.UTF8.GetBytes(Body)), "d")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Guid.CreateVersion7(), signature, "d")).StatusCode);
    }

    [Fact]
    public async Task Retried_delivery_returns_the_first_run_and_creates_no_second()
    {
        var (automation, trigger, secret) = await CreateAsync(policy: "allow");
        var signature = WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body));

        var first = await SendAsync(trigger.Id, signature, "delivery-42");
        var retry = await SendAsync(trigger.Id, signature, "delivery-42");
        var next = await SendAsync(trigger.Id, signature, "delivery-43");

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<RunAccepted>())!.RunId, (await retry.Content.ReadFromJsonAsync<RunAccepted>())!.RunId);
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        Assert.Equal(2L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Body_over_one_megabyte_is_rejected_before_any_run()
    {
        var (automation, trigger, secret) = await CreateAsync();
        var body = new string('x', 1024 * 1024 + 1);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{trigger.Id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(body)));
        message.Headers.Add(WebhookEndpoints.DeliveryIdHeader, "big");

        var response = await _sender.SendAsync(message);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Delivery_without_a_delivery_id_is_rejected()
    {
        var (automation, trigger, secret) = await CreateAsync();

        var response = await SendAsync(trigger.Id, WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body)), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Secret_is_shown_once_and_stored_encrypted()
    {
        var (automation, trigger, secret) = await CreateAsync();

        var read = await _api.Operator.GetFromJsonAsync<AutomationResponse>($"/api/automations/{automation.Id}");
        var stored = await HostData.ScalarAsync<byte[]>(pg, "SELECT webhook_secret FROM automation_triggers WHERE id = @id", ("id", trigger.Id));

        Assert.StartsWith(WebhookSignature.SecretPrefix, secret, StringComparison.Ordinal);
        Assert.Null(read!.Triggers.Single().WebhookSecret);
        Assert.Equal($"/api/webhooks/{trigger.Id}", read.Triggers.Single().WebhookUrl);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_the_secret_invalidates_the_old_one()
    {
        var (automation, trigger, oldSecret) = await CreateAsync(policy: "allow");

        var response = await _api.Operator.PostAsync($"/api/automations/{automation.Id}/triggers/{trigger.Id}/rotate-secret", null);
        var newSecret = (await response.Content.ReadFromJsonAsync<SecretRotated>())!.WebhookSecret;

        Assert.NotEqual(oldSecret, newSecret);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(trigger.Id, WebhookSignature.Compute(oldSecret, Encoding.UTF8.GetBytes(Body)), "d1")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await SendAsync(trigger.Id, WebhookSignature.Compute(newSecret, Encoding.UTF8.GetBytes(Body)), "d2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _api.Operator.PostAsync($"/api/automations/{Guid.CreateVersion7()}/triggers/{trigger.Id}/rotate-secret", null)).StatusCode);
    }

    [Fact]
    public async Task Delivery_under_skip_at_the_limit_is_acknowledged_as_skipped()
    {
        var (automation, trigger, secret) = await CreateAsync(policy: "skip");
        var signature = WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body));
        await SendAsync(trigger.Id, signature, "first");

        var response = await SendAsync(trigger.Id, signature, "second");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<WebhookSkipped>())!.Skipped);
        Assert.Equal(1L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Delivery_for_a_disabled_automation_is_a_conflict()
    {
        var (automation, trigger, secret) = await CreateAsync();
        await _api.Operator.PostAsync($"/api/automations/{automation.Id}/disable", null);

        var response = await SendAsync(trigger.Id, WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(Body)), "d");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await RunCountAsync(automation.Id));
    }

    private async Task<(AutomationResponse Automation, TriggerResponse Trigger, string Secret)> CreateAsync(string policy = "skip")
    {
        var (automation, _) = await _api.CreateAsync(_api.Request(triggers: [new TriggerRequest(null, "webhook")], policy: policy));
        var trigger = automation.Triggers.Single();
        return (automation, trigger, trigger.WebhookSecret!);
    }

    private async Task<HttpResponseMessage> SendAsync(Guid triggerId, string? signature, string? deliveryId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{triggerId}")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
            message.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, signature);
        if (deliveryId is not null)
            message.Headers.Add(WebhookEndpoints.DeliveryIdHeader, deliveryId);
        return await _sender.SendAsync(message);
    }

    private Task<long> RunCountAsync(Guid automationId) =>
        HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", automationId));
}
