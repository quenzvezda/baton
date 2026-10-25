using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Baton.Integration.Tests.Security;

/// <summary>NFR-Security, ADR-0014: every Control Plane endpoint declares a role policy or is explicitly anonymous.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class EndpointInventoryTests(PostgresFixture pg)
{
    [Fact]
    public async Task Every_endpoint_declares_a_role_policy_or_is_anonymous()
    {
        await using var api = await ControlPlaneHost.StartAsync(pg);

        var endpoints = api.Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "/api/hosts/");
        Assert.Empty(Unprotected(endpoints));
    }

    [Fact]
    public async Task Inventory_check_reports_an_endpoint_without_a_policy()
    {
        await using var api = await ControlPlaneHost.StartAsync(pg, app => app.MapGet("/api/forgotten", () => "oops"));

        var unprotected = Unprotected(api.Endpoints.OfType<RouteEndpoint>());

        Assert.Equal(["GET /api/forgotten"], unprotected);
    }

    /// <summary>Endpoints with neither <c>[AllowAnonymous]</c> nor a policy naming one of <see cref="BatonRoles"/>.</summary>
    private static List<string> Unprotected(IEnumerable<RouteEndpoint> endpoints) =>
    [
        .. endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null
                && !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is { } p && BatonRoles.All.Contains(p)))
            .Select(e => $"{string.Join(',', e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}"),
    ];
}
