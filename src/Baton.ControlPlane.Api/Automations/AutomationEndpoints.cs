using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Automations;
using Baton.Persistence;
using Baton.Persistence.Entities;
using Baton.Persistence.Queue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Baton.ControlPlane.Api.Automations;

/// <summary>A trigger in a create or update body; an <see cref="Id"/> keeps that existing trigger.</summary>
public sealed record TriggerRequest(Guid? Id, string? Kind, string? CronExpression = null, string? TimeZone = null, bool? Enabled = null);

/// <summary>Body of create and update (AUT-01..AUT-09). Absent optional settings take their defaults.</summary>
public sealed record AutomationRequest(
    string? Name,
    string? WorkflowKey,
    Guid? HostGroupId,
    TriggerRequest[]? Triggers,
    string? Description = null,
    bool? Enabled = null,
    string? Owner = null,
    int? WorkflowVersion = null,
    JsonElement? Parameters = null,
    string[]? RequiredLabels = null,
    int? Priority = null,
    int? MaxConcurrentRuns = null,
    string? ConcurrencyPolicy = null,
    TimeSpan? RunTimeout = null);

/// <param name="WebhookUrl">Where a webhook trigger receives deliveries.</param>
/// <param name="WebhookSecret">Only in the response that issued it (create, clone, rotate-secret).</param>
public sealed record TriggerResponse(
    Guid Id,
    string Kind,
    string? CronExpression,
    string? TimeZone,
    bool Enabled,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFiredAt,
    string? WebhookUrl,
    string? WebhookSecret);

/// <param name="Version">The value of the ETag; send it back in <c>If-Match</c> to update.</param>
public sealed record AutomationResponse(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    string Owner,
    string WorkflowKey,
    int? WorkflowVersion,
    JsonElement Parameters,
    Guid HostGroupId,
    string[] RequiredLabels,
    int Priority,
    int MaxConcurrentRuns,
    string ConcurrencyPolicy,
    TimeSpan? RunTimeout,
    DateTimeOffset UpdatedAt,
    int Version,
    TriggerResponse[] Triggers);

public sealed record CloneRequest(string? Name);

public sealed record RunAccepted(Guid RunId);

public sealed record SecretRotated(Guid TriggerId, string WebhookSecret);

/// <summary>
/// Automations (AUT-01..AUT-09) and Run now (AUT-04). Viewers read; Operators change, clone, enable, disable,
/// delete and run (design.md decision 12). Updates need <c>If-Match</c> with the ETag last read (412 when stale,
/// 428 when missing). Deleting is soft: the automation's runs carry on (decision 5).
/// </summary>
public static class AutomationEndpoints
{
    private const int MaxIdempotencyKeyLength = 200;

    public static IEndpointRouteBuilder MapAutomations(this IEndpointRouteBuilder app)
    {
        var automations = app.MapGroup("/api/automations");
        automations.MapGet("/", ListAsync).RequireAuthorization(BatonRoles.Viewer);
        automations.MapGet("/{id:guid}", GetAsync).RequireAuthorization(BatonRoles.Viewer);
        automations.MapPost("/", CreateAsync).RequireAuthorization(BatonRoles.Operator);
        automations.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(BatonRoles.Operator);
        automations.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(BatonRoles.Operator);
        automations.MapPost("/{id:guid}/clone", CloneAsync).RequireAuthorization(BatonRoles.Operator);
        automations.MapPost("/{id:guid}/enable", (Guid id, BatonDbContext db, HttpResponse response, CancellationToken ct) =>
            SetEnabledAsync(id, true, db, response, ct)).RequireAuthorization(BatonRoles.Operator);
        automations.MapPost("/{id:guid}/disable", (Guid id, BatonDbContext db, HttpResponse response, CancellationToken ct) =>
            SetEnabledAsync(id, false, db, response, ct)).RequireAuthorization(BatonRoles.Operator);
        automations.MapPost("/{id:guid}/runs", RunNowAsync).RequireAuthorization(BatonRoles.Operator);
        automations.MapPost("/{id:guid}/triggers/{triggerId:guid}/rotate-secret", RotateSecretAsync).RequireAuthorization(BatonRoles.Operator);
        return app;
    }

    private static async Task<Ok<AutomationResponse[]>> ListAsync(BatonDbContext db, CancellationToken ct)
    {
        var automations = await db.Automations.AsNoTracking().Where(a => a.DeletedAt == null).OrderBy(a => a.Name).ToListAsync(ct);
        var ids = automations.Select(a => a.Id).ToList();
        var triggers = await db.AutomationTriggers.AsNoTracking().Where(t => ids.Contains(t.AutomationId)).ToListAsync(ct);
        var byAutomation = triggers.ToLookup(t => t.AutomationId);
        return TypedResults.Ok(automations.Select(a => ToResponse(a, byAutomation[a.Id], null)).ToArray());
    }

    private static async Task<Results<Ok<AutomationResponse>, NotFound>> GetAsync(
        Guid id, BatonDbContext db, HttpResponse response, CancellationToken ct)
    {
        var automation = await db.Automations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (automation is null)
            return TypedResults.NotFound();

        var triggers = await db.AutomationTriggers.AsNoTracking().Where(t => t.AutomationId == id).ToListAsync(ct);
        return Ok(response, ToResponse(automation, triggers, null));
    }

    private static async Task<Results<Created<AutomationResponse>, ValidationProblem, Conflict<ProblemDetails>>> CreateAsync(
        AutomationRequest request, BatonDbContext db, WebhookSecrets secrets, ClaimsPrincipal user, HttpResponse response,
        CancellationToken ct)
    {
        var owner = string.IsNullOrWhiteSpace(request.Owner) ? CallerName(user) : request.Owner;
        var errors = await ValidateAsync(request, owner, db, ct);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var automation = new AutomationEntity
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name!.Trim(),
            WorkflowKey = request.WorkflowKey!,
            Owner = owner,
            Enabled = request.Enabled ?? true,
        };
        Apply(automation, request);

        var issued = new Dictionary<Guid, string>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = await DbNowAsync(db, ct);
        automation.UpdatedAt = now;
        db.Automations.Add(automation);
        foreach (var trigger in request.Triggers!)
            db.AutomationTriggers.Add(NewTrigger(automation.Id, trigger.Kind!, trigger.CronExpression, trigger.TimeZone, trigger.Enabled ?? true, now, secrets, issued));

        if (!await SaveAsync(db, ct))
            return DuplicateName(automation.Name);

        await SetLimitAsync(db, automation, ct);
        await transaction.CommitAsync(ct);

        var triggers = await db.AutomationTriggers.AsNoTracking().Where(t => t.AutomationId == automation.Id).ToListAsync(ct);
        SetETag(response, automation.RowVersion);
        return TypedResults.Created($"/api/automations/{automation.Id}", ToResponse(automation, triggers, issued));
    }

    private static async Task<Results<Ok<AutomationResponse>, NotFound, ValidationProblem, Conflict<ProblemDetails>, ProblemHttpResult>> UpdateAsync(
        Guid id, AutomationRequest request, BatonDbContext db, WebhookSecrets secrets, HttpRequest http, HttpResponse response,
        CancellationToken ct)
    {
        if (!TryReadIfMatch(http, out var expectedVersion))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match required",
                detail: "Send the ETag of the automation you last read in If-Match.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var automation = await db.Automations.SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (automation is null)
            return TypedResults.NotFound();
        if (automation.RowVersion != expectedVersion)
            return Stale();

        var owner = string.IsNullOrWhiteSpace(request.Owner) ? automation.Owner : request.Owner;
        var errors = await ValidateAsync(request, owner, db, ct);
        var existing = await db.AutomationTriggers.Where(t => t.AutomationId == id).ToListAsync(ct);
        ValidateTriggerIds(request.Triggers ?? [], existing, errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var now = await DbNowAsync(db, ct);
        var wasEnabled = automation.Enabled;
        automation.Name = request.Name!.Trim();
        automation.WorkflowKey = request.WorkflowKey!;
        automation.Owner = owner;
        automation.Enabled = request.Enabled ?? automation.Enabled;
        Apply(automation, request);
        automation.UpdatedAt = now;
        automation.RowVersion = expectedVersion + 1;

        var issued = new Dictionary<Guid, string>();
        var kept = new HashSet<Guid>();
        foreach (var trigger in request.Triggers!)
        {
            var current = trigger.Id is { } triggerId ? existing.Single(t => t.Id == triggerId) : null;
            if (current is null)
            {
                db.AutomationTriggers.Add(NewTrigger(id, trigger.Kind!, trigger.CronExpression, trigger.TimeZone, trigger.Enabled ?? true, now, secrets, issued));
                continue;
            }

            kept.Add(current.Id);
            var enabled = trigger.Enabled ?? current.Enabled;
            var scheduleChanged = current.CronExpr != trigger.CronExpression || current.TimeZone != trigger.TimeZone;
            var resumed = (enabled && !current.Enabled) || (automation.Enabled && !wasEnabled);
            (current.CronExpr, current.TimeZone, current.Enabled) = (trigger.CronExpression, trigger.TimeZone, enabled);
            if (current.Kind == AutomationRules.CronKind && (scheduleChanged || resumed || current.NextFireAt is null))
                current.NextFireAt = NextFire(current, now);
        }

        db.AutomationTriggers.RemoveRange(existing.Where(t => !kept.Contains(t.Id)));

        try
        {
            if (!await SaveAsync(db, ct))
                return DuplicateName(automation.Name);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        await SetLimitAsync(db, automation, ct);
        await transaction.CommitAsync(ct);

        var triggers = await db.AutomationTriggers.AsNoTracking().Where(t => t.AutomationId == id).ToListAsync(ct);
        return Ok(response, ToResponse(automation, triggers, issued));
    }

    /// <summary>Soft delete (design.md decision 5): triggers stop, runs carry on, the name becomes free.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id, BatonDbContext db, HttpRequest http, CancellationToken ct)
    {
        var automation = await db.Automations.SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (automation is null)
            return TypedResults.NotFound();
        if (http.Headers.IfMatch.Count > 0 && (!TryReadIfMatch(http, out var expected) || expected != automation.RowVersion))
            return Stale();

        automation.DeletedAt = await DbNowAsync(db, ct);
        automation.RowVersion++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        return TypedResults.NoContent();
    }

    /// <summary>AUT-01 "Clone": the same settings and triggers under a new name, disabled; webhook triggers get new secrets.</summary>
    private static async Task<Results<Created<AutomationResponse>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> CloneAsync(
        Guid id, CloneRequest request, BatonDbContext db, WebhookSecrets secrets, HttpResponse response, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > AutomationRules.MaxNameLength)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"Required, at most {AutomationRules.MaxNameLength} characters."] });

        var source = await db.Automations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (source is null)
            return TypedResults.NotFound();
        var sourceTriggers = await db.AutomationTriggers.AsNoTracking().Where(t => t.AutomationId == id).ToListAsync(ct);

        var clone = new AutomationEntity
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            Description = source.Description,
            Enabled = false,
            WorkflowKey = source.WorkflowKey,
            WorkflowVersion = source.WorkflowVersion,
            Parameters = source.Parameters,
            HostGroupId = source.HostGroupId,
            RequiredLabels = source.RequiredLabels,
            Priority = source.Priority,
            MaxConcurrentRuns = source.MaxConcurrentRuns,
            ConcurrencyPolicy = source.ConcurrencyPolicy,
            PlacementMode = source.PlacementMode,
            RunTimeout = source.RunTimeout,
            Owner = source.Owner,
        };

        var issued = new Dictionary<Guid, string>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = await DbNowAsync(db, ct);
        clone.UpdatedAt = now;
        db.Automations.Add(clone);
        foreach (var trigger in sourceTriggers)
            db.AutomationTriggers.Add(NewTrigger(clone.Id, trigger.Kind, trigger.CronExpr, trigger.TimeZone, trigger.Enabled, now, secrets, issued));

        if (!await SaveAsync(db, ct))
            return DuplicateName(clone.Name);

        await SetLimitAsync(db, clone, ct);
        await transaction.CommitAsync(ct);

        var triggers = await db.AutomationTriggers.AsNoTracking().Where(t => t.AutomationId == clone.Id).ToListAsync(ct);
        SetETag(response, clone.RowVersion);
        return TypedResults.Created($"/api/automations/{clone.Id}", ToResponse(clone, triggers, issued));
    }

    /// <summary>Enabling recomputes every cron trigger's next fire time from now, so missed times do not fire.</summary>
    private static async Task<Results<Ok<AutomationResponse>, NotFound, ProblemHttpResult>> SetEnabledAsync(
        Guid id, bool enabled, BatonDbContext db, HttpResponse response, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var automation = await db.Automations.SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (automation is null)
            return TypedResults.NotFound();

        var triggers = await db.AutomationTriggers.Where(t => t.AutomationId == id).ToListAsync(ct);
        if (automation.Enabled != enabled)
        {
            var now = await DbNowAsync(db, ct);
            automation.Enabled = enabled;
            automation.UpdatedAt = now;
            automation.RowVersion++;
            if (enabled)
            {
                foreach (var trigger in triggers.Where(t => t.Kind == AutomationRules.CronKind))
                    trigger.NextFireAt = NextFire(trigger, now);
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Stale();
            }
        }

        await transaction.CommitAsync(ct);
        return Ok(response, ToResponse(automation, triggers, null));
    }

    /// <summary>
    /// Run now (AUT-04) through the concurrency policy (design.md decisions 8, 10): 202 with the run, also for a
    /// repeated <c>Idempotency-Key</c>; 409 when the automation is disabled or Skip is at the limit.
    /// </summary>
    private static async Task<Results<Accepted<RunAccepted>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> RunNowAsync(
        Guid id, RunCreator runs, HttpRequest http, CancellationToken ct)
    {
        var requestKey = http.Headers["Idempotency-Key"].ToString();
        if (requestKey.Length > MaxIdempotencyKeyLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = [$"At most {MaxIdempotencyKeyLength} characters."],
            });
        }

        var key = $"automation:{id}:run-now:{(requestKey.Length > 0 ? requestKey : Guid.CreateVersion7().ToString())}";
        var result = await runs.FireAsync(new FireRequest(id, null, AutomationRules.ManualKind, key), ct);
        return result.Outcome switch
        {
            FireOutcome.Created or FireOutcome.Duplicate => TypedResults.Accepted((string?)null, new RunAccepted(result.RunId!.Value)),
            FireOutcome.AutomationNotFound => TypedResults.NotFound(),
            FireOutcome.AutomationDisabled => Conflict("Automation disabled", "Enable the automation to run it."),
            FireOutcome.Skipped => Conflict("Run skipped",
                "The automation is at its maximum concurrent runs and its policy is Skip; the skipped fire was recorded."),
            _ => Conflict("Workflow not registered", "The workflow version this automation selects is not registered."),
        };
    }

    private static async Task<Results<Ok<SecretRotated>, NotFound, ProblemHttpResult>> RotateSecretAsync(
        Guid id, Guid triggerId, BatonDbContext db, WebhookSecrets secrets, CancellationToken ct)
    {
        var (secret, stored) = secrets.Issue();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var automation = await db.Automations.SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        var trigger = await db.AutomationTriggers.SingleOrDefaultAsync(
            t => t.Id == triggerId && t.AutomationId == id && t.Kind == AutomationRules.WebhookKind, ct);
        if (automation is null || trigger is null)
            return TypedResults.NotFound();

        trigger.WebhookSecret = stored;
        automation.RowVersion++;
        automation.UpdatedAt = await DbNowAsync(db, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Ok(new SecretRotated(triggerId, secret));
    }

    private static async Task<Dictionary<string, string[]>> ValidateAsync(
        AutomationRequest request, string owner, BatonDbContext db, CancellationToken ct)
    {
        var settings = new AutomationSettings(
            request.Name, request.Description, owner, request.WorkflowKey, request.WorkflowVersion, request.HostGroupId,
            request.RequiredLabels, request.Priority ?? AutomationRules.DefaultPriority,
            request.MaxConcurrentRuns ?? AutomationRules.DefaultMaxConcurrentRuns,
            request.ConcurrencyPolicy ?? ConcurrencyPolicies.ToDb(ConcurrencyPolicy.Skip), request.RunTimeout,
            request.Triggers?.Select(t => new TriggerSettings(t?.Kind, t?.CronExpression, t?.TimeZone)).ToList());
        var errors = AutomationRules.Validate(settings);

        if (request.HostGroupId is { } groupId && !errors.ContainsKey("hostGroupId")
            && !await db.HostGroups.AnyAsync(g => g.Id == groupId, ct))
        {
            errors["hostGroupId"] = ["No host group has this id."];
        }

        if (errors.ContainsKey("workflowKey") || errors.ContainsKey("workflowVersion"))
            return errors;

        // AUT-02, AUT-03: the version a run would use if created now.
        var version = request.WorkflowVersion;
        var schema = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.Key == request.WorkflowKey && (version == null ? !d.IsRetired : d.Version == version))
            .OrderByDescending(d => d.Version)
            .Select(d => d.ParamsSchema)
            .FirstOrDefaultAsync(ct);
        if (schema is null)
        {
            errors[version is null ? "workflowKey" : "workflowVersion"] =
                [version is null ? $"No workflow '{request.WorkflowKey}' is registered." : $"'{request.WorkflowKey}@{version}' is not registered."];
            return errors;
        }

        var parameters = request.Parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } p
            ? p
            : JsonSerializer.SerializeToElement(new { });
        foreach (var (field, messages) in ParameterValidator.Validate(schema, parameters))
            errors[field] = messages;

        return errors;
    }

    private static void ValidateTriggerIds(TriggerRequest[] requested, List<AutomationTriggerEntity> existing, Dictionary<string, string[]> errors)
    {
        for (var i = 0; i < requested.Length; i++)
        {
            if (requested[i]?.Id is not { } triggerId)
                continue;

            var current = existing.SingleOrDefault(t => t.Id == triggerId);
            if (current is null)
                errors[$"triggers[{i}].id"] = ["This automation has no trigger with this id; leave it out to add a trigger."];
            else if (current.Kind != requested[i].Kind)
                errors[$"triggers[{i}].kind"] = ["A trigger's kind cannot change; remove it and add a new one."];
        }
    }

    private static void Apply(AutomationEntity automation, AutomationRequest request)
    {
        automation.Description = request.Description;
        automation.WorkflowVersion = request.WorkflowVersion;
        automation.Parameters = request.Parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } p
            ? p.GetRawText()
            : "{}";
        automation.HostGroupId = request.HostGroupId!.Value;
        automation.RequiredLabels = request.RequiredLabels?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        automation.Priority = (short)(request.Priority ?? AutomationRules.DefaultPriority);
        automation.MaxConcurrentRuns = request.MaxConcurrentRuns ?? AutomationRules.DefaultMaxConcurrentRuns;
        automation.ConcurrencyPolicy = request.ConcurrencyPolicy ?? ConcurrencyPolicies.ToDb(ConcurrencyPolicy.Skip);
        automation.RunTimeout = request.RunTimeout;
    }

    private static AutomationTriggerEntity NewTrigger(
        Guid automationId, string kind, string? cronExpression, string? timeZone, bool enabled, DateTimeOffset now,
        WebhookSecrets secrets, Dictionary<Guid, string> issued)
    {
        var trigger = new AutomationTriggerEntity
        {
            Id = Guid.CreateVersion7(),
            AutomationId = automationId,
            Kind = kind,
            CronExpr = cronExpression,
            TimeZone = timeZone,
            Enabled = enabled,
        };

        if (kind == AutomationRules.CronKind)
            trigger.NextFireAt = NextFire(trigger, now);

        if (kind == AutomationRules.WebhookKind)
        {
            var (secret, stored) = secrets.Issue();
            trigger.WebhookSecret = stored;
            issued[trigger.Id] = secret;
        }

        return trigger;
    }

    private static DateTimeOffset? NextFire(AutomationTriggerEntity trigger, DateTimeOffset now) =>
        CronSchedule.Create(trigger.CronExpr!, trigger.TimeZone!).NextAfter(now);

    /// <summary>The <c>automation:{id}</c> row follows <c>max_concurrent_runs</c> (ADR-0009).</summary>
    private static async Task SetLimitAsync(BatonDbContext db, AutomationEntity automation, CancellationToken ct)
    {
        var key = AutomationRules.AutomationLimitKey(automation.Id);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO concurrency_limits (key, max) VALUES ({key}, {automation.MaxConcurrentRuns}) ON CONFLICT (key) DO UPDATE SET max = EXCLUDED.max",
            ct);
    }

    /// <returns><c>false</c> when the name is taken by another automation that is not deleted.</returns>
    private static async Task<bool> SaveAsync(BatonDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    /// <summary>Times come from Postgres (AGENTS.md "Architecture invariants").</summary>
    private static Task<DateTimeOffset> DbNowAsync(BatonDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync(ct);

    private static string CallerName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity?.Name ?? "unknown";

    private static bool TryReadIfMatch(HttpRequest http, out int version)
    {
        version = 0;
        var value = http.Headers.IfMatch.ToString().Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        return value.Length > 2 && value[0] == '"' && value[^1] == '"'
            && int.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static void SetETag(HttpResponse response, int version) =>
        response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    private static Ok<AutomationResponse> Ok(HttpResponse response, AutomationResponse body)
    {
        SetETag(response, body.Version);
        return TypedResults.Ok(body);
    }

    private static AutomationResponse ToResponse(
        AutomationEntity a, IEnumerable<AutomationTriggerEntity> triggers, IReadOnlyDictionary<Guid, string>? issuedSecrets) => new(
        a.Id,
        a.Name,
        a.Description,
        a.Enabled,
        a.Owner,
        a.WorkflowKey,
        a.WorkflowVersion,
        JsonSerializer.Deserialize<JsonElement>(a.Parameters),
        a.HostGroupId,
        a.RequiredLabels,
        a.Priority,
        a.MaxConcurrentRuns,
        a.ConcurrencyPolicy,
        a.RunTimeout,
        a.UpdatedAt,
        a.RowVersion,
        [
            .. triggers.OrderBy(t => t.Id).Select(t => new TriggerResponse(
                t.Id, t.Kind, t.CronExpr, t.TimeZone, t.Enabled, t.NextFireAt, t.LastFiredAt,
                t.Kind == AutomationRules.WebhookKind ? $"/api/webhooks/{t.Id}" : null,
                issuedSecrets?.GetValueOrDefault(t.Id))),
        ]);

    private static ProblemHttpResult Stale() => TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
        title: "Automation changed", detail: "The automation changed since you read it; reload it and apply your change again.");

    private static Conflict<ProblemDetails> DuplicateName(string name) =>
        Conflict("Duplicate automation", $"An automation named '{name}' already exists.");

    private static Conflict<ProblemDetails> Conflict(string title, string detail) =>
        TypedResults.Conflict(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status409Conflict });
}
