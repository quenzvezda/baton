using Baton.Domain.Automations;
using Baton.Persistence;
using Baton.Persistence.Queue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Automations;

public sealed record WebhookSkipped(bool Skipped);

/// <summary>
/// Webhook intake (AUT-04, ORC-01, design.md decisions 2, 3, 7, 8). Anonymous: the HMAC-SHA256 signature of the
/// raw body in <c>X-Signature</c> is the authentication, and an unknown trigger answers like a bad signature
/// (401) so trigger ids cannot be probed. <c>X-Delivery-Id</c> identifies the delivery: a retry returns the run
/// the first request created. The body itself is not used in M4.
/// </summary>
public static class WebhookEndpoints
{
    public const string DeliveryIdHeader = "X-Delivery-Id";

    private const int MaxDeliveryIdLength = 200;
    private const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/{triggerId:guid}", ReceiveAsync).AllowAnonymous();
        return app;
    }

    private static async Task<Results<Accepted<RunAccepted>, Ok<WebhookSkipped>, UnauthorizedHttpResult, ValidationProblem, Conflict<ProblemDetails>, StatusCodeHttpResult>> ReceiveAsync(
        Guid triggerId, HttpRequest http, BatonDbContext db, WebhookSecrets secrets, RunCreator runs, CancellationToken ct)
    {
        var body = await ReadBodyAsync(http, ct);
        if (body is null)
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var trigger = await db.AutomationTriggers.AsNoTracking()
            .Where(t => t.Id == triggerId && t.Kind == AutomationRules.WebhookKind)
            .Join(db.Automations.Where(a => a.DeletedAt == null), t => t.AutomationId, a => a.Id,
                (t, a) => new { t.AutomationId, t.WebhookSecret, TriggerEnabled = t.Enabled, AutomationEnabled = a.Enabled })
            .SingleOrDefaultAsync(ct);
        var secret = trigger?.WebhookSecret is { } stored ? secrets.Reveal(stored) : null;
        if (trigger is null || secret is null
            || !WebhookSignature.Verify(secret, body, http.Headers[WebhookSignature.HeaderName]))
        {
            return TypedResults.Unauthorized();
        }

        var deliveryId = http.Headers[DeliveryIdHeader].ToString();
        if (deliveryId.Length is 0 or > MaxDeliveryIdLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [DeliveryIdHeader] = [$"Required: a unique id per delivery, at most {MaxDeliveryIdLength} characters, the same on retries."],
            });
        }

        if (!trigger.TriggerEnabled)
            return Conflict("Trigger disabled", "This webhook trigger is disabled.");

        var key = $"automation:{trigger.AutomationId}:trigger:{triggerId}:delivery:{deliveryId}";
        var result = await runs.FireAsync(new FireRequest(trigger.AutomationId, triggerId, AutomationRules.WebhookKind, key), ct);
        return result.Outcome switch
        {
            FireOutcome.Created or FireOutcome.Duplicate => TypedResults.Accepted((string?)null, new RunAccepted(result.RunId!.Value)),
            FireOutcome.Skipped => TypedResults.Ok(new WebhookSkipped(true)),
            FireOutcome.AutomationDisabled => Conflict("Automation disabled", "The automation of this webhook is disabled."),
            FireOutcome.AutomationNotFound => TypedResults.Unauthorized(),
            _ => Conflict("Workflow not registered", "The workflow version the automation selects is not registered."),
        };
    }

    /// <returns>The raw body, or <c>null</c> when it is larger than <see cref="MaxBodyBytes"/>; never buffers more than that.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest http, CancellationToken ct)
    {
        if (http.ContentLength > MaxBodyBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Conflict<ProblemDetails> Conflict(string title, string detail) =>
        TypedResults.Conflict(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status409Conflict });
}
