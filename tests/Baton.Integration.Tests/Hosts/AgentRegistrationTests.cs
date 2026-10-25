using Baton.Domain.Hosts;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Hosts;
using Grpc.Core;

namespace Baton.Integration.Tests.Hosts;

/// <summary>HST-02 and NFR-Security: <c>AgentService.Register</c> with group-scoped credentials (ADR-0013).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AgentRegistrationTests(PostgresFixture pg) : IAsyncLifetime
{
    private OrchestratorHost _orchestrator = null!;
    private HostDirectory _hosts = null!;

    public async Task InitializeAsync()
    {
        _orchestrator = await OrchestratorHost.StartAsync(pg);
        _hosts = new HostDirectory(pg.DataSource);
    }

    public async Task DisposeAsync() => await _orchestrator.DisposeAsync();

    [Fact]
    public async Task First_registration_creates_an_online_host()
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg, "etl");
        var (credentialId, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var name = HostData.NewName("etl-01");

        var reply = await _orchestrator.RegisterAsync(token, name, group, slots: 4, version: "1.2.0",
            labels: ["large-memory"], hashes: ["hash-a", "hash-b"]);

        Assert.Equal(TimeSpan.FromSeconds(60), reply.LeaseDuration.ToTimeSpan());
        Assert.Equal(HostLiveness.HeartbeatInterval, reply.HeartbeatInterval.ToTimeSpan());
        Assert.False(reply.Draining);

        var host = await _hosts.FindAsync(Guid.Parse(reply.HostId), CancellationToken.None);
        Assert.NotNull(host);
        Assert.Equal(name, host.Name);
        Assert.Equal(groupId, host.HostGroupId);
        Assert.Equal(HostStatus.Online, host.Status);
        Assert.Equal(4, host.SlotsTotal);
        Assert.Equal(["large-memory"], host.Labels);
        Assert.Equal("1.2.0", host.AgentVersion);
        Assert.Equal(["hash-a", "hash-b"], host.DefinitionHashes);
        Assert.Equal(credentialId, await HostData.ScalarAsync<Guid>(pg,
            "SELECT credential_id FROM hosts WHERE id = @id", ("id", host.Id)));
    }

    [Fact]
    public async Task Re_registration_after_an_upgrade_updates_the_same_host()
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var name = HostData.NewName("host");
        var first = await _orchestrator.RegisterAsync(token, name, group, version: "1.0.0", hashes: ["old"]);

        var second = await _orchestrator.RegisterAsync(token, name, group, slots: 8, version: "2.0.0", hashes: ["new"]);

        Assert.Equal(first.HostId, second.HostId);
        var host = await _hosts.FindAsync(Guid.Parse(second.HostId), CancellationToken.None);
        Assert.Equal("2.0.0", host!.AgentVersion);
        Assert.Equal(["new"], host.DefinitionHashes);
        Assert.Equal(8, host.SlotsTotal);
        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM hosts WHERE name = @name", ("name", name)));
    }

    [Fact]
    public async Task Concurrent_registrations_of_one_name_create_one_host()
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var name = HostData.NewName("host");

        var replies = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => _orchestrator.RegisterAsync(token, name, group, version: $"1.0.{i}")));

        Assert.Single(replies.Select(r => r.HostId).Distinct());
        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM hosts WHERE name = @name", ("name", name)));
    }

    [Fact]
    public async Task Unknown_group_is_rejected()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var name = HostData.NewName("host");

        var error = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.RegisterAsync(token, name, "no-such-group"));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.False(await HostExistsAsync(name));
    }

    [Fact]
    public async Task Missing_credential_is_unauthenticated_and_records_nothing()
    {
        var (_, group) = await HostData.CreateGroupAsync(pg);
        var name = HostData.NewName("host");

        var error = await Assert.ThrowsAsync<RpcException>(async () => await _orchestrator.Client.RegisterAsync(
            new Agent.Protocol.V1.RegisterRequest { HostName = name, HostGroup = group, CapacitySlots = 1, AgentVersion = "1" }));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.False(await HostExistsAsync(name));
    }

    [Fact]
    public async Task Unknown_token_is_unauthenticated()
    {
        var (_, group) = await HostData.CreateGroupAsync(pg);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            _orchestrator.RegisterAsync(AgentToken.Generate(), HostData.NewName("host"), group));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task Credential_for_another_group_is_permission_denied()
    {
        var (etlId, _) = await HostData.CreateGroupAsync(pg, "etl");
        var (_, gpu) = await HostData.CreateGroupAsync(pg, "gpu");
        var (_, etlToken) = await HostData.IssueCredentialAsync(pg, etlId);
        var name = HostData.NewName("host");

        var error = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.RegisterAsync(etlToken, name, gpu));

        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.False(await HostExistsAsync(name));
    }

    [Fact]
    public async Task Another_groups_credential_cannot_take_over_a_host()
    {
        var (etlId, etl) = await HostData.CreateGroupAsync(pg, "etl");
        var (gpuId, gpu) = await HostData.CreateGroupAsync(pg, "gpu");
        var (_, etlToken) = await HostData.IssueCredentialAsync(pg, etlId);
        var (_, gpuToken) = await HostData.IssueCredentialAsync(pg, gpuId);
        var name = HostData.NewName("gpu-01");
        var gpuHost = await _orchestrator.RegisterAsync(gpuToken, name, gpu, version: "1.0.0");

        var takeover = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.RegisterAsync(etlToken, name, etl, version: "6.6.6"));
        var heartbeat = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.HeartbeatAsync(etlToken, gpuHost.HostId));

        Assert.Equal(StatusCode.PermissionDenied, takeover.StatusCode);
        Assert.Equal(StatusCode.NotFound, heartbeat.StatusCode);
        var host = await _hosts.FindAsync(Guid.Parse(gpuHost.HostId), CancellationToken.None);
        Assert.Equal(gpuId, host!.HostGroupId);
        Assert.Equal("1.0.0", host.AgentVersion);
    }

    [Fact]
    public async Task Restart_after_a_move_keeps_the_new_group()
    {
        var (etlId, etl) = await HostData.CreateGroupAsync(pg, "etl");
        var (gpuId, _) = await HostData.CreateGroupAsync(pg, "gpu");
        var (_, etlToken) = await HostData.IssueCredentialAsync(pg, etlId);
        var name = HostData.NewName("etl-01");
        var registered = await _orchestrator.RegisterAsync(etlToken, name, etl);
        var hostId = Guid.Parse(registered.HostId);
        await _hosts.MoveAsync(hostId, gpuId, CancellationToken.None);

        var restarted = await _orchestrator.RegisterAsync(etlToken, name, etl, version: "1.1.0");
        await _orchestrator.HeartbeatAsync(etlToken, restarted.HostId);

        Assert.Equal(registered.HostId, restarted.HostId);
        var host = await _hosts.FindAsync(hostId, CancellationToken.None);
        Assert.Equal(gpuId, host!.HostGroupId);
        Assert.Equal("1.1.0", host.AgentVersion);
    }

    [Theory]
    [InlineData("", 1, "1.0.0")]
    [InlineData("host", 0, "1.0.0")]
    [InlineData("host", 1, "")]
    public async Task Invalid_registration_is_rejected(string name, int slots, string version)
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);

        var error = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.RegisterAsync(token, name, group, slots, version));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    private Task<bool> HostExistsAsync(string name) =>
        HostData.ScalarAsync<bool>(pg, "SELECT EXISTS (SELECT 1 FROM hosts WHERE name = @name)", ("name", name));
}
