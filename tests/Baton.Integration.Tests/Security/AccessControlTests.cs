using System.Net;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Baton.Integration.Tests.Security;

/// <summary>NFR-Security: OIDC bearer tokens and the three roles on the Control Plane API (ADR-0014).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AccessControlTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;

    public async Task InitializeAsync() => _api = await ControlPlaneHost.StartAsync(pg);

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Theory]
    [InlineData("/api/workflows")]
    [InlineData("/api/host-groups")]
    [InlineData("/api/hosts")]
    public async Task Anonymous_request_is_unauthorized(string path)
    {
        using var http = _api.Client();

        var response = await http.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_unauthorized()
    {
        var foreignKey = new SymmetricSecurityKey(new byte[32]);
        using var http = _api.ClientWithToken(TestTokens.Create(foreignKey, TestTokens.ClaimValue(BatonRoles.Admin)));

        var response = await http.GetAsync(new Uri("/api/hosts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_check_is_anonymous()
    {
        using var http = _api.Client();

        var response = await http.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(BatonRoles.Viewer)]
    [InlineData(BatonRoles.Operator)]
    [InlineData(BatonRoles.Admin)]
    public async Task Every_role_reads(string role)
    {
        using var http = _api.Client(role);

        var response = await http.GetAsync(new Uri("/api/hosts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_drain_a_host()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, groupId);
        using var http = _api.Client(BatonRoles.Viewer);

        var response = await http.PostAsync(new Uri($"/api/hosts/{hostId}/drain", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await HostData.ScalarAsync<bool>(pg, "SELECT drain_requested FROM hosts WHERE id = @id", ("id", hostId)));
    }

    [Fact]
    public async Task Operator_cannot_manage_host_groups()
    {
        var (groupId, name) = await HostData.CreateGroupAsync(pg);
        using var http = _api.Client(BatonRoles.Operator);

        var response = await http.PutAsJsonAsync(new Uri($"/api/host-groups/{groupId}", UriKind.Relative),
            new HostGroupRequest(name, 5, 20, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT max_concurrent_runs IS NULL FROM host_groups WHERE id = @id", ("id", groupId)));
    }

    [Fact]
    public async Task Admin_manages_host_groups()
    {
        var (groupId, name) = await HostData.CreateGroupAsync(pg);
        using var http = _api.Client(BatonRoles.Admin);

        var response = await http.PutAsJsonAsync(new Uri($"/api/host-groups/{groupId}", UriKind.Relative),
            new HostGroupRequest(name, 5, 20, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_without_a_mapped_role_is_forbidden()
    {
        using var http = _api.ClientWithToken(TestTokens.Create("some-other-app.admin"));

        var response = await http.GetAsync(new Uri("/api/hosts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Role_claim_issued_by_the_caller_grants_nothing()
    {
        // A token may carry any claim; only roles Baton derives from the IdP's role claim count.
        var token = TestTokens.CreateWithClaims(TestTokens.SigningKey, new Dictionary<string, object>
        {
            [BatonRoles.ClaimType] = BatonRoles.Admin,
        });
        using var http = _api.ClientWithToken(token);

        var response = await http.GetAsync(new Uri("/api/hosts", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
