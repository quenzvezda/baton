using Baton.ControlPlane.Api.Security;
using Baton.Persistence.Runs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Baton.ControlPlane.Api.Queue;

/// <summary>OBS-04 "Queue statistics" (M7 design.md decision 24): every host group's queue, ordered by name. Viewer.</summary>
public static class QueueEndpoints
{
    public static IEndpointRouteBuilder MapQueue(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/queue/stats", async (QueueStats stats, CancellationToken ct) =>
            TypedResults.Ok(await stats.ReadAsync(ct))).RequireAuthorization(BatonRoles.Viewer);
        return app;
    }
}
