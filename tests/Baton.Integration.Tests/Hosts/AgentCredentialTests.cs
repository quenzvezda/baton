using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Hosts;
using Baton.Integration.Tests.Infrastructure;
using Grpc.Core;

namespace Baton.Integration.Tests.Hosts;

/// <summary>NFR-Security, ADR-0013: Admins issue and revoke group-scoped agent credentials.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AgentCredentialTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;
    private OrchestratorHost _orchestrator = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _api = await ControlPlaneHost.StartAsync(pg);
        _orchestrator = await OrchestratorHost.StartAsync(pg);
        _admin = _api.Client(BatonRoles.Admin);
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _orchestrator.DisposeAsync();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Issued_token_is_shown_once_and_stored_only_as_a_hash()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);

        var response = await _admin.PostAsJsonAsync(Url($"/api/host-groups/{groupId}/agent-tokens"), new AgentTokenRequest("etl-agents"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var issued = await response.Content.ReadFromJsonAsync<AgentTokenIssued>();
        Assert.StartsWith(AgentToken.Prefix, issued!.Token);
        Assert.Equal("etl-agents", issued.Name);

        var stored = await HostData.ScalarAsync<byte[]>(pg, "SELECT token_hash FROM agent_credentials WHERE id = @id", ("id", issued.Id));
        Assert.Equal(AgentToken.Hash(issued.Token), stored);

        var listed = await _admin.GetFromJsonAsync<JsonElement>(Url($"/api/host-groups/{groupId}/agent-tokens"));
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal(issued.Id, entry.GetProperty("id").GetGuid());
        Assert.False(entry.TryGetProperty("token", out _));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task Duplicate_token_name_in_a_group_is_a_conflict()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        await _admin.PostAsJsonAsync(Url($"/api/host-groups/{groupId}/agent-tokens"), new AgentTokenRequest("same"));

        var response = await _admin.PostAsJsonAsync(Url($"/api/host-groups/{groupId}/agent-tokens"), new AgentTokenRequest("same"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_is_rejected()
    {
        var (groupId, groupName) = await HostData.CreateGroupAsync(pg);
        var issued = await (await _admin.PostAsJsonAsync(Url($"/api/host-groups/{groupId}/agent-tokens"), new AgentTokenRequest("rotate-me")))
            .Content.ReadFromJsonAsync<AgentTokenIssued>();
        var registered = await _orchestrator.RegisterAsync(issued!.Token, HostData.NewName("host"), groupName);
        await HostData.AgeHeartbeatAsync(pg, Guid.Parse(registered.HostId), TimeSpan.FromMinutes(5));
        var before = await LastHeartbeatAsync(registered.HostId);

        var revoke = await _admin.DeleteAsync(Url($"/api/host-groups/{groupId}/agent-tokens/{issued.Id}"));

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var heartbeat = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.HeartbeatAsync(issued.Token, registered.HostId));
        Assert.Equal(StatusCode.Unauthenticated, heartbeat.StatusCode);
        var register = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.RegisterAsync(issued.Token, HostData.NewName("host"), groupName));
        Assert.Equal(StatusCode.Unauthenticated, register.StatusCode);
        Assert.Equal(before, await LastHeartbeatAsync(registered.HostId));
    }

    [Fact]
    public async Task Operator_cannot_issue_tokens()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        using var http = _api.Client(BatonRoles.Operator);

        var response = await http.PostAsJsonAsync(Url($"/api/host-groups/{groupId}/agent-tokens"), new AgentTokenRequest("nope"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM agent_credentials WHERE host_group_id = @id", ("id", groupId)));
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private Task<DateTimeOffset> LastHeartbeatAsync(string hostId) =>
        HostData.ScalarAsync<DateTimeOffset>(pg, "SELECT last_heartbeat_at FROM hosts WHERE id = @id", ("id", Guid.Parse(hostId)));
}
