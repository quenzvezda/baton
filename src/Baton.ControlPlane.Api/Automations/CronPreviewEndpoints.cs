using System.Globalization;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Automations;
using Baton.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Baton.ControlPlane.Api.Automations;

/// <param name="At">The fire time as an instant (UTC).</param>
/// <param name="Local">The same time on the wall clock of the trigger's zone, <c>yyyy-MM-ddTHH:mm:ss</c>.</param>
/// <param name="Offset">The zone's UTC offset at that time, <c>+08:00</c>.</param>
public sealed record FireTimePreview(DateTimeOffset At, string Local, string Offset);

/// <summary>
/// AUT-10 (design.md decision 8): the next fire times of a cron expression in its zone, after the database's
/// <c>now()</c>, for the editor to show while the expression is typed. Read-only, so any role may call it.
/// </summary>
public static class CronPreviewEndpoints
{
    public const int Count = 5;

    public static IEndpointRouteBuilder MapCronPreview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cron/preview", PreviewAsync).RequireAuthorization(BatonRoles.Viewer);
        return app;
    }

    private static async Task<Results<Ok<FireTimePreview[]>, ValidationProblem>> PreviewAsync(
        string? expression, string? timeZone, BatonDbContext db, CancellationToken ct)
    {
        var schedule = CronSchedule.TryCreate(expression, timeZone, out var errors);
        if (schedule is null)
            return TypedResults.ValidationProblem(errors.ToDictionary(e => e.Key, e => new[] { e.Value }));

        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"").SingleAsync(ct);
        var times = new List<FireTimePreview>(Count);
        for (var at = schedule.NextAfter(now); at is { } next && times.Count < Count; at = schedule.NextAfter(next))
        {
            var local = TimeZoneInfo.ConvertTime(next, schedule.Zone);
            times.Add(new FireTimePreview(next, local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                local.ToString("zzz", CultureInfo.InvariantCulture)));
        }

        return TypedResults.Ok(times.ToArray());
    }
}
