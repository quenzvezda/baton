using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Baton.ControlPlane.Api.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// JWT bearer authentication against the OIDC provider in <c>Authentication:Schemes:Bearer</c> and
    /// one policy per role (ADR-0014). Endpoints without a policy fall back to Viewer.
    /// </summary>
    public static IServiceCollection AddBatonSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BatonAuthOptions>(configuration.GetSection(BatonAuthOptions.Section));
        services.AddTransient<IClaimsTransformation, BatonRoleClaimsTransformation>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            // Keep claim types as issued, so Baton:Auth:RoleClaimType names the provider's own claim.
            .AddJwtBearer(options => options.MapInboundClaims = false);

        var authorization = services.AddAuthorizationBuilder();
        foreach (var role in BatonRoles.All)
            authorization.AddPolicy(role, RolePolicy(role));
        authorization.SetFallbackPolicy(RolePolicy(BatonRoles.Viewer));

        return services;
    }

    private static AuthorizationPolicy RolePolicy(string role) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => BatonRoles.Has(context.User, role))
        .Build();
}
