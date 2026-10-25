using System.Security.Claims;

namespace Baton.ControlPlane.Api.Security;

/// <summary>
/// The three control-plane roles (NFR-Security) and the authorization policies named after them.
/// Roles are hierarchical: Admin implies Operator implies Viewer (ADR-0014).
/// </summary>
public static class BatonRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    /// <summary>Claim type of the roles Baton grants, on its own identity (see <see cref="IdentityType"/>).</summary>
    public const string ClaimType = "baton_role";

    /// <summary>
    /// Authentication type of the identity <see cref="BatonRoleClaimsTransformation"/> adds. Policies only
    /// trust role claims on this identity, so a token that carries a <c>baton_role</c> claim of its own
    /// grants nothing.
    /// </summary>
    public const string IdentityType = "baton-roles";

    /// <summary>Lowest to highest.</summary>
    public static readonly IReadOnlyList<string> All = [Viewer, Operator, Admin];

    /// <summary>The role itself and every role below it.</summary>
    public static IEnumerable<string> Implied(string role) => All.Take(IndexOf(role) + 1);

    public static bool Has(ClaimsPrincipal user, string role) =>
        user.Identities.Any(i => i.AuthenticationType == IdentityType && i.HasClaim(ClaimType, role));

    private static int IndexOf(string role)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == role)
                return i;
        }
        throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");
    }
}
