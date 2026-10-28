using Baton.ControlPlane.Api.Security;
using Baton.Persistence.Queue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Baton.ControlPlane.Api.Runs;

/// <param name="Status"><c>cancelled</c> for a Pending run, else <c>cancelling</c> until its leased tasks stop.</param>
public sealed record CancelAccepted(Guid RunId, string Status);

/// <summary>Run actions. Reading runs (OBS-01, OBS-02) arrives with the console in M5.</summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRuns(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/api/runs");
        runs.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(BatonRoles.Operator);
        return app;
    }

    /// <summary>ORC-11: 202 while the run stops, 409 when it had already finished.</summary>
    private static async Task<Results<Accepted<CancelAccepted>, NotFound, Conflict<ProblemDetails>>> CancelAsync(
        Guid id, RunCancellation cancellation, CancellationToken ct) =>
        await cancellation.CancelAsync(id, RunCancelReason.User, ct) switch
        {
            CancelOutcome.NotFound => TypedResults.NotFound(),
            CancelOutcome.AlreadyFinished => TypedResults.Conflict(new ProblemDetails
            {
                Title = "Run already finished",
                Detail = "Only a pending or running run can be cancelled.",
                Status = StatusCodes.Status409Conflict,
            }),
            CancelOutcome.Cancelled => TypedResults.Accepted((string?)null, new CancelAccepted(id, "cancelled")),
            _ => TypedResults.Accepted((string?)null, new CancelAccepted(id, "cancelling")),
        };
}
