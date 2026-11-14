using System.Security.Claims;
using Baton.ControlPlane.Api.Audit;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Automations;
using Baton.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Workflows;

public sealed record WorkflowLimitRequest(int? MaxConcurrentRuns);

/// <summary>A workflow limit as the audit log records it (AUT-11): the setting, not the usage that moves on its own.</summary>
public sealed record WorkflowLimitAudit(string WorkflowKey, int MaxConcurrentRuns);

/// <param name="MaxConcurrentRuns"><c>null</c> = unlimited.</param>
/// <param name="InUse">Runs of the workflow holding the limit now.</param>
public sealed record WorkflowLimitResponse(string WorkflowKey, int? MaxConcurrentRuns, int InUse);

/// <summary>
/// A workflow's run limit (ORC-02, design.md decision 4): row <c>workflow:{key}</c> in <c>concurrency_limits</c>,
/// applied to runs of every version. Operational, not part of the definition, so it never changes a hash.
/// Viewers read it; Admins set and remove it.
/// </summary>
public static class WorkflowLimitEndpoints
{
    private const int MaxLimit = 10_000;

    public static IEndpointRouteBuilder MapWorkflowLimits(this IEndpointRouteBuilder app)
    {
        var limit = app.MapGroup("/api/workflows/{key}/limit");
        limit.MapGet("/", GetAsync).RequireAuthorization(BatonRoles.Viewer);
        limit.MapPut("/", SetAsync).RequireAuthorization(BatonRoles.Admin);
        limit.MapDelete("/", RemoveAsync).RequireAuthorization(BatonRoles.Admin);
        return app;
    }

    private static async Task<Results<Ok<WorkflowLimitResponse>, NotFound>> GetAsync(string key, BatonDbContext db, CancellationToken ct)
    {
        if (!await db.WorkflowDefinitions.AnyAsync(d => d.Key == key, ct))
            return TypedResults.NotFound();

        var row = await db.ConcurrencyLimits.AsNoTracking().SingleOrDefaultAsync(l => l.Key == AutomationRules.WorkflowLimitKey(key), ct);
        return TypedResults.Ok(new WorkflowLimitResponse(key, row?.Max, row?.InUse ?? 0));
    }

    /// <summary>Lowering the limit below what is in use is allowed; new runs wait until it drains.</summary>
    private static async Task<Results<Ok<WorkflowLimitResponse>, NotFound, ValidationProblem>> SetAsync(
        string key, WorkflowLimitRequest request, BatonDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.MaxConcurrentRuns is not (>= 1 and <= MaxLimit))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["maxConcurrentRuns"] = [$"Must be between 1 and {MaxLimit}; DELETE the limit for unlimited."],
            });
        }

        if (!await db.WorkflowDefinitions.AnyAsync(d => d.Key == key, ct))
            return TypedResults.NotFound();

        var limitKey = AutomationRules.WorkflowLimitKey(key);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var previous = await db.ConcurrencyLimits.AsNoTracking().Where(l => l.Key == limitKey).Select(l => (int?)l.Max).SingleOrDefaultAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO concurrency_limits (key, max) VALUES ({limitKey}, {request.MaxConcurrentRuns.Value}) ON CONFLICT (key) DO UPDATE SET max = EXCLUDED.max",
            ct);
        var row = await db.ConcurrencyLimits.AsNoTracking().SingleAsync(l => l.Key == limitKey, ct);
        if (previous != row.Max)
        {
            AuditWriter.Add(db, user, AuditEntities.WorkflowLimit, key, AuditActions.Set,
                previous is { } max ? new WorkflowLimitAudit(key, max) : null, new WorkflowLimitAudit(key, row.Max));
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Ok(new WorkflowLimitResponse(key, row.Max, row.InUse));
    }

    /// <summary>Runs that hold the row keep it until they end; releasing a missing row is a no-op.</summary>
    private static async Task<Results<NoContent, NotFound>> RemoveAsync(
        string key, BatonDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.WorkflowDefinitions.AnyAsync(d => d.Key == key, ct))
            return TypedResults.NotFound();

        var limitKey = AutomationRules.WorkflowLimitKey(key);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var previous = await db.ConcurrencyLimits.AsNoTracking().Where(l => l.Key == limitKey).Select(l => (int?)l.Max).SingleOrDefaultAsync(ct);
        if (previous is { } max)
        {
            await db.ConcurrencyLimits.Where(l => l.Key == limitKey).ExecuteDeleteAsync(ct);
            AuditWriter.Add(db, user, AuditEntities.WorkflowLimit, key, AuditActions.Remove, new WorkflowLimitAudit(key, max), null);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }
}
