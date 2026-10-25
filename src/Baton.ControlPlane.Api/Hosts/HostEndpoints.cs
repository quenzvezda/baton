using Baton.ControlPlane.Api.Security;
using Baton.Persistence;
using Baton.Persistence.Hosts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Hosts;

public sealed record MoveHostRequest(Guid HostGroupId);

/// <summary>
/// Host inventory (HST-06), drain and undrain (HST-04, Operator), group move (HST-05, Admin) and
/// removal of Offline hosts (Admin). The hosts page arrives in M5.
/// </summary>
public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHosts(this IEndpointRouteBuilder app)
    {
        var hosts = app.MapGroup("/api/hosts");
        hosts.MapGet("/", ListAsync).RequireAuthorization(BatonRoles.Viewer);
        hosts.MapGet("/{id:guid}", GetAsync).RequireAuthorization(BatonRoles.Viewer);
        hosts.MapPost("/{id:guid}/drain", DrainAsync).RequireAuthorization(BatonRoles.Operator);
        hosts.MapPost("/{id:guid}/undrain", UndrainAsync).RequireAuthorization(BatonRoles.Operator);
        hosts.MapPut("/{id:guid}/group", MoveAsync).RequireAuthorization(BatonRoles.Admin);
        hosts.MapDelete("/{id:guid}", RemoveAsync).RequireAuthorization(BatonRoles.Admin);
        return app;
    }

    private static async Task<Ok<IReadOnlyList<HostView>>> ListAsync(HostDirectory hosts, CancellationToken ct) =>
        TypedResults.Ok(await hosts.ListAsync(ct));

    private static async Task<Results<Ok<HostView>, NotFound>> GetAsync(Guid id, HostDirectory hosts, CancellationToken ct) =>
        await hosts.FindAsync(id, ct) is { } host ? TypedResults.Ok(host) : TypedResults.NotFound();

    /// <summary>The agent learns it from its next heartbeat reply and stops claiming; running tasks finish.</summary>
    private static async Task<Results<NoContent, NotFound>> DrainAsync(Guid id, HostDirectory hosts, CancellationToken ct) =>
        await hosts.DrainAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<NoContent, NotFound>> UndrainAsync(Guid id, HostDirectory hosts, CancellationToken ct) =>
        await hosts.UndrainAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> MoveAsync(
        Guid id, MoveHostRequest request, HostDirectory hosts, BatonDbContext db, CancellationToken ct)
    {
        if (!await db.HostGroups.AnyAsync(g => g.Id == request.HostGroupId, ct))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hostGroupId"] = ["No host group has this id."],
            });
        }

        return await hosts.MoveAsync(id, request.HostGroupId, ct) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> RemoveAsync(
        Guid id, HostDirectory hosts, CancellationToken ct) =>
        await hosts.RemoveAsync(id, ct) switch
        {
            HostRemoval.Removed => TypedResults.NoContent(),
            HostRemoval.NotFound => TypedResults.NotFound(),
            HostRemoval.NotOffline => Conflict("Host is not offline", "Only an Offline host can be removed; drain and stop its agent first."),
            _ => Conflict("Host in use", "A run is pinned to this host."),
        };

    private static Conflict<ProblemDetails> Conflict(string title, string detail) =>
        TypedResults.Conflict(new ProblemDetails { Title = title, Detail = detail, Status = StatusCodes.Status409Conflict });
}
