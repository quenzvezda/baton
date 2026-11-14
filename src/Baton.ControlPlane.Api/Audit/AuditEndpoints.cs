using System.Text.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Audit;

public sealed record AuditActorResponse(string Subject, string Name);

public sealed record AuditEntryResponse(
    long Id, DateTimeOffset At, AuditActorResponse Actor, string EntityType, string EntityId, string Action,
    JsonElement? Before, JsonElement? After);

public sealed record AuditPage(AuditEntryResponse[] Items, string? NextCursor);

/// <summary>
/// AUT-11 "Audit log query" (design.md decision 15): the entries of one entity type, optionally one entity, newest
/// first, cursor-paged and filtered by time. Viewer.
/// </summary>
public static class AuditEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static IEndpointRouteBuilder MapAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", ListAsync).RequireAuthorization(BatonRoles.Viewer);
        return app;
    }

    private static async Task<Results<Ok<AuditPage>, ValidationProblem>> ListAsync(
        BatonDbContext db, CancellationToken ct, string? entityType = null, string? entityId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? cursor = null, int? limit = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (entityType is null || !AuditEntities.All.Contains(entityType))
            errors["entityType"] = [$"Required, one of {string.Join(", ", AuditEntities.All)}."];
        if (limit is < 1 or > MaxLimit)
            errors["limit"] = [$"Between 1 and {MaxLimit}."];
        AuditCursor? position = null;
        if (cursor is not null && !AuditCursor.TryDecode(cursor, out position))
            errors["cursor"] = ["Not a cursor this endpoint returned."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var take = limit ?? DefaultLimit;
        var query = db.AuditLog.AsNoTracking().Where(a => a.EntityType == entityType);
        if (!string.IsNullOrEmpty(entityId))
            query = query.Where(a => a.EntityId == entityId);
        if (from is { } since)
        {
            var utc = since.ToUniversalTime();
            query = query.Where(a => a.At >= utc);
        }

        if (to is { } until)
        {
            var utc = until.ToUniversalTime();
            query = query.Where(a => a.At < utc);
        }

        if (position is not null)
            query = query.Where(a => a.At < position.At || (a.At == position.At && a.Id < position.Id));

        var rows = await query.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).Select(a => new AuditEntryResponse(
            a.Id, a.At, new AuditActorResponse(a.ActorSubject, a.ActorName), a.EntityType, a.EntityId, a.Action,
            a.Before is null ? null : JsonSerializer.Deserialize<JsonElement>(a.Before),
            a.After is null ? null : JsonSerializer.Deserialize<JsonElement>(a.After))).ToArray();
        var next = rows.Count > take ? new AuditCursor(page[^1].At, page[^1].Id).Encode() : null;
        return TypedResults.Ok(new AuditPage(page, next));
    }
}
