namespace Baton.ControlPlane.Api.Security;

/// <summary>
/// Maps the identity provider's role claim onto Baton roles (ADR-0014), bound from <c>Baton:Auth</c>.
/// The provider itself is configured by <c>Authentication:Schemes:Bearer</c>.
/// </summary>
public sealed class BatonAuthOptions
{
    public const string Section = "Baton:Auth";

    /// <summary>Claim that carries the caller's roles, as issued (inbound claim mapping is off).</summary>
    public string RoleClaimType { get; set; } = "roles";

    /// <summary>Claim values that grant each role, keyed by <see cref="BatonRoles"/> name.</summary>
    public Dictionary<string, string[]> Roles { get; set; } = new(StringComparer.Ordinal);
}
