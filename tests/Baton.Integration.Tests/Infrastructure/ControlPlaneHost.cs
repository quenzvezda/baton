using System.Net.Http.Headers;
using Baton.ControlPlane.Api;
using Baton.ControlPlane.Api.Security;
using Baton.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>
/// The Control Plane API (<see cref="ControlPlane"/>) on a real Kestrel host on a free port, against the
/// shared Postgres, with JWTs signed by <see cref="TestTokens"/>.
/// </summary>
public sealed class ControlPlaneHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ControlPlaneHost(WebApplication app)
    {
        _app = app;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public static async Task<ControlPlaneHost> StartAsync(PostgresFixture pg, Action<WebApplication>? mapExtra = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{BatonAuthOptions.Section}:RoleClaimType"] = TestTokens.RoleClaimType,
            [$"{BatonAuthOptions.Section}:Roles:{BatonRoles.Viewer}:0"] = TestTokens.ClaimValue(BatonRoles.Viewer),
            [$"{BatonAuthOptions.Section}:Roles:{BatonRoles.Operator}:0"] = TestTokens.ClaimValue(BatonRoles.Operator),
            [$"{BatonAuthOptions.Section}:Roles:{BatonRoles.Admin}:0"] = TestTokens.ClaimValue(BatonRoles.Admin),
        });
        builder.Services.AddBatonPersistence(pg.ConnectionString);
        builder.Services.AddControlPlane(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TestTokens.Issuer,
                ValidAudience = TestTokens.Audience,
                IssuerSigningKey = TestTokens.SigningKey,
            };
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControlPlane();
        mapExtra?.Invoke(app);
        await app.StartAsync();
        return new ControlPlaneHost(app);
    }

    /// <summary>A client with no credentials, or with a token granting <paramref name="role"/>.</summary>
    public HttpClient Client(string? role = null)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        if (role is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForRole(role));
        return client;
    }

    public HttpClient ClientWithToken(string token)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public IServiceProvider Services => _app.Services;

    public IEnumerable<Microsoft.AspNetCore.Http.Endpoint> Endpoints =>
        ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
