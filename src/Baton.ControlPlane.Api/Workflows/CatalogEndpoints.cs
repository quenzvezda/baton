using System.Text.Json;
using Baton.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Workflows;

/// <summary>A key's latest registered version (WF-09).</summary>
public sealed record WorkflowSummary(
    string Key, int LatestVersion, string DisplayName, bool IsRetired, DateTimeOffset RegisteredAt);

/// <summary>One registered <c>key@version</c>: its task graph, params schema and all versions of the key (WF-09).</summary>
public sealed record WorkflowVersionDetail(
    string Key,
    int Version,
    string DisplayName,
    string GraphHash,
    JsonElement Graph,
    JsonElement ParamsSchema,
    bool IsRetired,
    DateTimeOffset RegisteredAt,
    IReadOnlyList<int> Versions);

/// <summary>Read-only workflow catalog API (WF-09). The catalog page arrives in M5.</summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowCatalog(this IEndpointRouteBuilder app)
    {
        var workflows = app.MapGroup("/api/workflows");
        workflows.MapGet("/", ListLatestAsync);
        workflows.MapGet("/{key}/versions/{version:int}", GetVersionAsync);
        return app;
    }

    private static async Task<Ok<WorkflowSummary[]>> ListLatestAsync(BatonDbContext db, CancellationToken ct)
    {
        var latest = await db.WorkflowDefinitions
            .AsNoTracking()
            .Where(d => !db.WorkflowDefinitions.Any(newer => newer.Key == d.Key && newer.Version > d.Version))
            .OrderBy(d => d.Key)
            .Select(d => new WorkflowSummary(d.Key, d.Version, d.DisplayName, d.IsRetired, d.RegisteredAt))
            .ToArrayAsync(ct);

        return TypedResults.Ok(latest);
    }

    private static async Task<Results<Ok<WorkflowVersionDetail>, NotFound>> GetVersionAsync(
        string key, int version, BatonDbContext db, CancellationToken ct)
    {
        var definition = await db.WorkflowDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.Key == key && d.Version == version, ct);
        if (definition is null)
            return TypedResults.NotFound();

        var versions = await db.WorkflowDefinitions
            .Where(d => d.Key == key)
            .OrderBy(d => d.Version)
            .Select(d => d.Version)
            .ToListAsync(ct);

        return TypedResults.Ok(new WorkflowVersionDetail(
            definition.Key,
            definition.Version,
            definition.DisplayName,
            definition.GraphHash,
            JsonSerializer.Deserialize<JsonElement>(definition.Graph),
            JsonSerializer.Deserialize<JsonElement>(definition.ParamsSchema),
            definition.IsRetired,
            definition.RegisteredAt,
            versions));
    }
}
