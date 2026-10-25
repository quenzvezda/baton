using Baton.Domain.Hosts;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Hosts;
using Grpc.Core;

namespace Baton.Integration.Tests.Hosts;

/// <summary>HST-03 and HST-04: heartbeats, liveness from Postgres time, and drain state in the reply.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HeartbeatTests(PostgresFixture pg) : IAsyncLifetime
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
    public async Task Heartbeat_time_comes_from_the_database_not_the_agent()
    {
        var (token, hostId) = await RegisterHostAsync();
        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromDays(365));

        await _orchestrator.HeartbeatAsync(token, hostId.ToString());

        // The request carries no time at all; the row must hold the server's now(), to the transaction.
        var skew = await HostData.ScalarAsync<TimeSpan>(pg,
            "SELECT now() - last_heartbeat_at FROM hosts WHERE id = @id", ("id", hostId));
        Assert.InRange(skew, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task No_heartbeat_for_31_seconds_is_offline_and_not_eligible()
    {
        var (_, hostId) = await RegisterHostAsync();
        Assert.True(await HostData.IsEligibleAsync(pg, hostId));

        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromSeconds(31));

        Assert.Equal(HostStatus.Offline, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);
        Assert.False(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Heartbeat_29_seconds_ago_is_still_online()
    {
        var (_, hostId) = await RegisterHostAsync();

        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromSeconds(29));

        Assert.Equal(HostStatus.Online, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);
        Assert.True(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Offline_host_that_heartbeats_again_is_online()
    {
        var (token, hostId) = await RegisterHostAsync();
        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromMinutes(10));

        var reply = await _orchestrator.HeartbeatAsync(token, hostId.ToString());

        Assert.False(reply.Draining);
        Assert.Equal(HostStatus.Online, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);
        Assert.True(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Drained_host_learns_it_from_the_heartbeat_reply_and_is_not_eligible()
    {
        var (token, hostId) = await RegisterHostAsync();

        await _hosts.DrainAsync(hostId, CancellationToken.None);
        var drained = await _orchestrator.HeartbeatAsync(token, hostId.ToString());
        await _hosts.UndrainAsync(hostId, CancellationToken.None);
        var undrained = await _orchestrator.HeartbeatAsync(token, hostId.ToString());

        Assert.True(drained.Draining);
        Assert.False(undrained.Draining);
        Assert.True(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Drain_survives_an_outage()
    {
        var (token, hostId) = await RegisterHostAsync();
        await _hosts.DrainAsync(hostId, CancellationToken.None);
        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromMinutes(10));
        Assert.Equal(HostStatus.Offline, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);

        var reply = await _orchestrator.HeartbeatAsync(token, hostId.ToString());

        Assert.True(reply.Draining);
        Assert.Equal(HostStatus.Draining, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);
        Assert.False(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Drained_host_that_registers_again_is_still_draining()
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var name = HostData.NewName("host");
        var first = await _orchestrator.RegisterAsync(token, name, group);
        await _hosts.DrainAsync(Guid.Parse(first.HostId), CancellationToken.None);

        var again = await _orchestrator.RegisterAsync(token, name, group);

        Assert.True(again.Draining);
        Assert.Equal(HostStatus.Draining, (await _hosts.FindAsync(Guid.Parse(again.HostId), CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Agent_shutting_down_shows_as_draining()
    {
        var (token, hostId) = await RegisterHostAsync();

        var reply = await _orchestrator.HeartbeatAsync(token, hostId.ToString(), draining: true);

        Assert.False(reply.Draining);
        Assert.Equal(HostStatus.Draining, (await _hosts.FindAsync(hostId, CancellationToken.None))!.Status);
        Assert.False(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Heartbeat_for_an_unknown_host_is_not_found()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);

        var error = await Assert.ThrowsAsync<RpcException>(() => _orchestrator.HeartbeatAsync(token, Guid.CreateVersion7().ToString()));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_without_a_credential_is_unauthenticated()
    {
        var (_, hostId) = await RegisterHostAsync();

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await _orchestrator.Client.HeartbeatAsync(new Agent.Protocol.V1.HeartbeatRequest { HostId = hostId.ToString() }));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    private async Task<(string Token, Guid HostId)> RegisterHostAsync()
    {
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var reply = await _orchestrator.RegisterAsync(token, HostData.NewName("host"), group);
        return (token, Guid.Parse(reply.HostId));
    }
}
