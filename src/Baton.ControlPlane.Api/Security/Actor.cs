using System.Security.Claims;

namespace Baton.ControlPlane.Api.Security;

/// <summary>The person behind a request (ADR-0014): the OIDC <c>sub</c> and a display name, as <c>/api/me</c> shows it.</summary>
public sealed record Actor(string Subject, string Name)
{
    public static Actor From(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? subject ?? "unknown";
        return new Actor(subject ?? name, name);
    }
}
