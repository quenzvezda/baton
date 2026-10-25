using Baton.ControlPlane.Api.Security;
using Baton.Domain.Hosts;
using Baton.Persistence;
using Baton.Persistence.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Baton.ControlPlane.Api.Hosts;

/// <summary>Body of create and update; an absent limit means unlimited (HST-01).</summary>
public sealed record HostGroupRequest(string? Name, int? MaxConcurrentRuns, int? MaxConcurrentTasks, int? LeaseSeconds);

public sealed record HostGroupResponse(
    Guid Id, string Name, int? MaxConcurrentRuns, int? MaxConcurrentTasks, int LeaseSeconds, bool DispatchPaused);

public sealed record AgentTokenRequest(string? Name);

/// <summary>The only response that ever contains the token (ADR-0013).</summary>
public sealed record AgentTokenIssued(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);

public sealed record AgentTokenSummary(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Host groups and their limits (HST-01) and the group's agent credentials (NFR-Security, ADR-0013).
/// Viewers read groups; everything else is Admin.
/// </summary>
public static class HostGroupEndpoints
{
    private const int MaxTokenNameLength = 100;

    public static IEndpointRouteBuilder MapHostGroups(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/host-groups");
        groups.MapGet("/", ListAsync).RequireAuthorization(BatonRoles.Viewer);
        groups.MapGet("/{id:guid}", GetAsync).RequireAuthorization(BatonRoles.Viewer);
        groups.MapPost("/", CreateAsync).RequireAuthorization(BatonRoles.Admin);
        groups.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(BatonRoles.Admin);
        groups.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(BatonRoles.Admin);

        groups.MapGet("/{id:guid}/agent-tokens", ListTokensAsync).RequireAuthorization(BatonRoles.Admin);
        groups.MapPost("/{id:guid}/agent-tokens", IssueTokenAsync).RequireAuthorization(BatonRoles.Admin);
        groups.MapDelete("/{id:guid}/agent-tokens/{tokenId:guid}", RevokeTokenAsync).RequireAuthorization(BatonRoles.Admin);
        return app;
    }

    private static async Task<Ok<HostGroupResponse[]>> ListAsync(BatonDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.HostGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => ToResponse(g)).ToArrayAsync(ct));

    private static async Task<Results<Ok<HostGroupResponse>, NotFound>> GetAsync(Guid id, BatonDbContext db, CancellationToken ct)
    {
        var group = await db.HostGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, ct);
        return group is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<Created<HostGroupResponse>, ValidationProblem, Conflict<ProblemDetails>>> CreateAsync(
        HostGroupRequest request, BatonDbContext db, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var group = new HostGroupEntity { Id = Guid.CreateVersion7(), Name = request.Name! };
        Apply(group, request);
        db.HostGroups.Add(group);

        if (!await SaveWithLimitsAsync(db, group, ct))
            return DuplicateName(request.Name!);

        return TypedResults.Created($"/api/host-groups/{group.Id}", ToResponse(group));
    }

    private static async Task<Results<Ok<HostGroupResponse>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> UpdateAsync(
        Guid id, HostGroupRequest request, BatonDbContext db, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var group = await db.HostGroups.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
            return TypedResults.NotFound();

        group.Name = request.Name!;
        Apply(group, request);

        if (!await SaveWithLimitsAsync(db, group, ct))
            return DuplicateName(request.Name!);

        return TypedResults.Ok(ToResponse(group));
    }

    private static async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> DeleteAsync(
        Guid id, BatonDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var group = await db.HostGroups.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
            return TypedResults.NotFound();

        if (await db.Hosts.AnyAsync(h => h.HostGroupId == id, ct) || await db.Automations.AnyAsync(a => a.HostGroupId == id, ct))
            return InUse(group.Name);

        var limitKeys = new[] { HostGroupRules.RunsLimitKey(id), HostGroupRules.TasksLimitKey(id) };
        await db.ConcurrencyLimits.Where(l => limitKeys.Contains(l.Key)).ExecuteDeleteAsync(ct);

        // Credentials go with the group (ON DELETE CASCADE); runs that used it still reference it.
        db.HostGroups.Remove(group);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return InUse(group.Name);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AgentTokenSummary[]>, NotFound>> ListTokensAsync(Guid id, BatonDbContext db, CancellationToken ct)
    {
        if (!await db.HostGroups.AnyAsync(g => g.Id == id, ct))
            return TypedResults.NotFound();

        var tokens = await db.AgentCredentials.AsNoTracking()
            .Where(c => c.HostGroupId == id)
            .OrderBy(c => c.Name)
            .Select(c => new AgentTokenSummary(c.Id, c.Name, c.CreatedAt, c.RevokedAt))
            .ToArrayAsync(ct);
        return TypedResults.Ok(tokens);
    }

    private static async Task<Results<Created<AgentTokenIssued>, NotFound, ValidationProblem, Conflict<ProblemDetails>>> IssueTokenAsync(
        Guid id, AgentTokenRequest request, BatonDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxTokenNameLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Required, at most {MaxTokenNameLength} characters."],
            });
        }

        if (!await db.HostGroups.AnyAsync(g => g.Id == id, ct))
            return TypedResults.NotFound();

        var token = AgentToken.Generate();
        var credential = new AgentCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            HostGroupId = id,
            Name = request.Name,
            TokenHash = AgentToken.Hash(token),
        };
        db.AgentCredentials.Add(credential);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return TypedResults.Conflict(Problem("Duplicate token name", $"Group already has a token named '{request.Name}'."));
        }

        await db.Entry(credential).ReloadAsync(ct);
        return TypedResults.Created(
            $"/api/host-groups/{id}/agent-tokens/{credential.Id}",
            new AgentTokenIssued(credential.Id, credential.Name, token, credential.CreatedAt));
    }

    /// <summary>Revocation is immediate: agents are checked against the database on every call.</summary>
    private static async Task<Results<NoContent, NotFound>> RevokeTokenAsync(Guid id, Guid tokenId, BatonDbContext db, CancellationToken ct)
    {
        var found = await db.AgentCredentials.Where(c => c.Id == tokenId && c.HostGroupId == id).AnyAsync(ct);
        if (!found)
            return TypedResults.NotFound();

        await db.Database.ExecuteSqlAsync(
            $"UPDATE agent_credentials SET revoked_at = now() WHERE id = {tokenId} AND revoked_at IS NULL", ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Saves the group and upserts its limit rows in one transaction (ADR-0009); a <c>null</c> limit
    /// deletes the row, i.e. unlimited. <c>false</c> when the name is taken.
    /// </summary>
    private static async Task<bool> SaveWithLimitsAsync(BatonDbContext db, HostGroupEntity group, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }

        await SetLimitAsync(db, HostGroupRules.RunsLimitKey(group.Id), group.MaxConcurrentRuns, ct);
        await SetLimitAsync(db, HostGroupRules.TasksLimitKey(group.Id), group.MaxConcurrentTasks, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Lowering a limit below <c>in_use</c> is allowed; new work waits until it drains (design.md).</summary>
    private static async Task SetLimitAsync(BatonDbContext db, string key, int? max, CancellationToken ct)
    {
        if (max is { } value)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO concurrency_limits (key, max) VALUES ({key}, {value}) ON CONFLICT (key) DO UPDATE SET max = EXCLUDED.max",
                ct);
        }
        else
        {
            await db.ConcurrencyLimits.Where(l => l.Key == key).ExecuteDeleteAsync(ct);
        }
    }

    private static Dictionary<string, string[]> Validate(HostGroupRequest request) =>
        HostGroupRules.Validate(request.Name, request.MaxConcurrentRuns, request.MaxConcurrentTasks,
            request.LeaseSeconds ?? HostGroupRules.DefaultLeaseSeconds);

    private static void Apply(HostGroupEntity group, HostGroupRequest request)
    {
        group.MaxConcurrentRuns = request.MaxConcurrentRuns;
        group.MaxConcurrentTasks = request.MaxConcurrentTasks;
        group.LeaseSeconds = request.LeaseSeconds ?? HostGroupRules.DefaultLeaseSeconds;
    }

    private static HostGroupResponse ToResponse(HostGroupEntity g) =>
        new(g.Id, g.Name, g.MaxConcurrentRuns, g.MaxConcurrentTasks, g.LeaseSeconds, g.DispatchPaused);

    private static Conflict<ProblemDetails> DuplicateName(string name) =>
        TypedResults.Conflict(Problem("Duplicate host group", $"A host group named '{name}' already exists."));

    private static Conflict<ProblemDetails> InUse(string name) =>
        TypedResults.Conflict(Problem("Host group in use", $"Host group '{name}' still has hosts, automations or runs."));

    private static ProblemDetails Problem(string title, string detail) =>
        new() { Title = title, Detail = detail, Status = StatusCodes.Status409Conflict };
}
