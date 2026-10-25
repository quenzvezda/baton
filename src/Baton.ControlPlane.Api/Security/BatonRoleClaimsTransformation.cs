using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Baton.ControlPlane.Api.Security;

/// <summary>
/// Adds a <see cref="BatonRoles.IdentityType"/> identity holding the Baton roles the caller's IdP claims
/// grant, including every implied lower role.
/// </summary>
internal sealed class BatonRoleClaimsTransformation(IOptionsMonitor<BatonAuthOptions> options) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || principal.Identities.Any(i => i.AuthenticationType == BatonRoles.IdentityType))
        {
            return Task.FromResult(principal);
        }

        var settings = options.CurrentValue;
        var values = principal.FindAll(settings.RoleClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (role, claimValues) in settings.Roles)
        {
            if (BatonRoles.All.Contains(role) && claimValues.Any(values.Contains))
                granted.UnionWith(BatonRoles.Implied(role));
        }

        var transformed = principal.Clone();
        transformed.AddIdentity(new ClaimsIdentity(
            granted.Select(r => new Claim(BatonRoles.ClaimType, r)), BatonRoles.IdentityType));
        return Task.FromResult(transformed);
    }
}
