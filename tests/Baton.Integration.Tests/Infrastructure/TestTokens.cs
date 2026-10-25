using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>Signs JWTs the way an OIDC provider would, with a key only the tests know (ADR-0014).</summary>
public static class TestTokens
{
    public const string Issuer = "https://idp.baton.test";
    public const string Audience = "baton-api";
    public const string RoleClaimType = "roles";

    /// <summary>IdP claim value for each Baton role; <see cref="ControlPlaneHost"/> maps them.</summary>
    public static string ClaimValue(string role) => $"baton.{role.ToLowerInvariant()}";

    public static readonly SymmetricSecurityKey SigningKey = new(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());

    public static string Create(params string[] roleClaimValues) => Create(SigningKey, roleClaimValues);

    public static string Create(SecurityKey key, params string[] roleClaimValues) =>
        CreateWithClaims(key, new Dictionary<string, object> { [RoleClaimType] = roleClaimValues });

    public static string CreateWithClaims(SecurityKey key, Dictionary<string, object> extraClaims)
    {
        var claims = new Dictionary<string, object>(extraClaims) { ["sub"] = "test-user" };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }

    public static string ForRole(string role) => Create(ClaimValue(role));
}
