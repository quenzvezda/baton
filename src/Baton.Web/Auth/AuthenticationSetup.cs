using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Baton.Web.Auth;

/// <summary>OIDC sign-in settings, bound from <c>Oidc</c>. Any OIDC provider; Keycloak locally (ADR-0017).</summary>
public sealed class OidcSettings
{
    public const string Section = "Oidc";

    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Only for confidential clients; the local client is public and uses PKCE.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Extra scopes, e.g. the one some providers need to issue a token for the API audience.</summary>
    public string[] Scopes { get; set; } = [];

    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthenticationSetup
{
    /// <summary>
    /// Cookie session + OIDC authorization code with PKCE (ADR-0012, ADR-0014). Every page requires a signed-in
    /// person; the tokens are kept in the session for <see cref="AccessTokenProvider"/>.
    /// </summary>
    public static IServiceCollection AddBatonSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(OidcSettings.Section).Get<OidcSettings>() ?? new OidcSettings();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "baton.session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.Scope.Add("profile");
                foreach (var scope in settings.Scopes)
                    options.Scope.Add(scope);
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddHttpContextAccessor();
        services.AddScoped<AccessTokenProvider>();
        services.AddScoped<UserSession>();
        return services;
    }

    /// <summary><c>/account/login?returnUrl=</c> starts the sign-in; <c>POST /account/logout</c> ends it at the provider too.</summary>
    public static IEndpointRouteBuilder MapBatonSignIn(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account");

        account.MapGet("/login", (string? returnUrl) => TypedResults.Challenge(
                new AuthenticationProperties { RedirectUri = LocalOrRoot(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        account.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        });

        return app;
    }

    /// <summary>Only local paths, so the login link cannot redirect elsewhere.</summary>
    private static string LocalOrRoot(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
