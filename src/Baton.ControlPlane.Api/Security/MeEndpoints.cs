using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Baton.ControlPlane.Api.Security;

/// <param name="Roles">Effective Baton roles, implied ones included, lowest first.</param>
public sealed record CurrentUser(string Name, IReadOnlyList<string> Roles);

/// <summary>
/// The signed-in caller and their effective roles, so clients such as the console hide what the API would
/// refuse without repeating the claim-to-role mapping (ADR-0012, ADR-0014).
/// </summary>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", Get).RequireAuthorization(BatonRoles.Viewer);
        return app;
    }

    private static Ok<CurrentUser> Get(ClaimsPrincipal user) =>
        TypedResults.Ok(new CurrentUser(
            user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? user.FindFirst("sub")?.Value ?? "unknown",
            [.. BatonRoles.All.Where(role => BatonRoles.Has(user, role))]));
}
