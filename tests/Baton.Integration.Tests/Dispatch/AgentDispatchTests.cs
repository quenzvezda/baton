using System.Diagnostics;
using Baton.Agent.Protocol.V1;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>The M3 calls of <c>AgentService</c> over gRPC: Claim long-poll, Heartbeat leases, Start, Complete, AppendLogs.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AgentDispatchTests(PostgresFixture pg) : IAsyncLifetime
{
    private OrchestratorHost _orchestrator = null!;

    public async Task InitializeAsync() => _orchestrator = await OrchestratorHost.StartAsync(pg);

    public async Task DisposeAsync() => await _orchestrator.DisposeAsync();

    [Fact]
    public async Task Register_reply_derives_the_heartbeat_interval_from_the_lease()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), leaseSeconds: 30);
        var (_, token) = await HostData.IssueCredentialAsync(pg, s.GroupId);

        var reply = await _orchestrator.RegisterAsync(token, HostData.NewName("host"), s.GroupName);

        Assert.Equal(TimeSpan.FromSeconds(30), reply.LeaseDuration.ToTimeSpan());
        Assert.Equal(TimeSpan.FromSeconds(5), reply.HeartbeatInterval.ToTimeSpan());
    }

    [Fact]
    public async Task Claim_returns_the_lease_with_params_timeout_and_token()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a").Timeout(TimeSpan.FromMinutes(7)));
        var runId = await s.StartRunAsync(parameters: """{"keepDays":7}""");

        var reply = await ClaimAsync(token, hostId, TimeSpan.Zero);

        var lease = Assert.Single(reply.Leases);
        Assert.Equal(runId.ToString(), lease.WorkflowRunId);
        Assert.Equal("a", lease.TaskKey);
        Assert.Equal(s.Hash, lease.DefinitionHash);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal(TimeSpan.FromMinutes(7), lease.Timeout.ToTimeSpan());
        Assert.Equal("""{"keepDays": 7}""", lease.ParamsJson.ToStringUtf8());
        Assert.Equal((await s.TaskAsync(runId, "a")).LeaseToken, lease.LeaseToken);
    }

    [Fact]
    public async Task Claim_with_nothing_queued_returns_empty_after_max_wait()
    {
        var (_, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        var clock = Stopwatch.StartNew();

        var reply = await ClaimAsync(token, hostId, TimeSpan.FromMilliseconds(500));

        Assert.Empty(reply.Leases);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(450), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Claim_for_an_unregistered_host_is_not_found()
    {
        var (_, token, _) = await RegisteredAsync(wf => wf.Task<Noop>("a"));

        var error = await Assert.ThrowsAsync<RpcException>(() => ClaimAsync(token, Guid.CreateVersion7().ToString(), TimeSpan.Zero));

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Waiting_agent_is_woken_and_leased_within_2_seconds_at_p95()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        var latencies = new List<TimeSpan>();

        for (var i = 0; i < 20; i++)
        {
            var claim = ClaimAsync(token, hostId, TimeSpan.FromSeconds(20));
            await Task.Delay(150);
            var queued = Stopwatch.StartNew();
            var runId = await s.StartRunAsync();
            var reply = await claim;
            latencies.Add(queued.Elapsed);

            var lease = Assert.Single(reply.Leases);
            Assert.Equal(runId.ToString(), lease.WorkflowRunId);
            await s.Leases.CompleteAsync(s.GroupId,
                DispatchScenario.Report(Guid.Parse(lease.TaskRunId), lease.LeaseToken, Domain.Runs.AttemptOutcome.Succeeded),
                CancellationToken.None);
        }

        var p95 = latencies.Order().ElementAt((int)Math.Ceiling(latencies.Count * 0.95) - 1);
        Assert.True(p95 < TimeSpan.FromSeconds(2), $"p95 Queued→Leased was {p95}; all: {string.Join(", ", latencies)}");
    }

    [Fact]
    public async Task Notification_wakes_the_claim_long_before_the_recheck()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg, d => d.ClaimRecheck = TimeSpan.FromSeconds(30));
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var (_, token) = await HostData.IssueCredentialAsync(pg, s.GroupId);
        var hostId = (await orchestrator.RegisterAsync(token, HostData.NewName("host"), s.GroupName, hashes: [s.Hash])).HostId;
        await Task.Delay(500); // the listener has connected

        var claim = orchestrator.Client.ClaimAsync(
            new ClaimRequest { HostId = hostId, FreeSlots = 1, MaxWait = Duration.FromTimeSpan(TimeSpan.FromSeconds(20)) },
            OrchestratorHost.Bearer(token)).ResponseAsync;
        await Task.Delay(300);
        var queued = Stopwatch.StartNew();
        await s.StartRunAsync();
        var reply = await claim;

        Assert.Single(reply.Leases);
        Assert.True(queued.Elapsed < TimeSpan.FromSeconds(1), $"Leased after {queued.Elapsed}");
    }

    [Fact]
    public async Task Heartbeat_reports_lost_and_cancelled_leases()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        await s.StartRunAsync();
        await s.StartRunAsync();
        await s.StartRunAsync();
        var leases = (await ClaimAsync(token, hostId, TimeSpan.Zero)).Leases;
        await s.ExpireLeaseAsync(Guid.Parse(leases[0].TaskRunId));
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested = true WHERE id = @id", ("id", Guid.Parse(leases[1].TaskRunId)));

        var reply = await _orchestrator.Client.HeartbeatAsync(new HeartbeatRequest
        {
            HostId = hostId,
            Leases = { leases.Select(l => new LeaseRef { TaskRunId = l.TaskRunId, LeaseToken = l.LeaseToken }) },
        }, OrchestratorHost.Bearer(token));

        Assert.Equal([leases[0].TaskRunId], reply.LostTaskIds);
        Assert.Equal([leases[1].TaskRunId], reply.CancelTaskIds);
    }

    [Fact]
    public async Task Start_and_complete_with_the_current_token_succeed_the_task()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        var runId = await s.StartRunAsync();
        var lease = Assert.Single((await ClaimAsync(token, hostId, TimeSpan.Zero)).Leases);

        await _orchestrator.Client.StartAsync(new StartRequest { TaskRunId = lease.TaskRunId, LeaseToken = lease.LeaseToken },
            OrchestratorHost.Bearer(token));
        await _orchestrator.Client.CompleteAsync(new CompleteRequest
        {
            TaskRunId = lease.TaskRunId,
            LeaseToken = lease.LeaseToken,
            Outcome = TaskOutcome.Succeeded,
            OutputJson = ByteString.CopyFromUtf8("""{"purged":3}"""),
        }, OrchestratorHost.Bearer(token));

        Assert.Equal("""{"purged": 3}""", (await s.TaskAsync(runId, "a")).Output);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Stale_complete_is_failed_precondition()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        await s.StartRunAsync();
        var lease = Assert.Single((await ClaimAsync(token, hostId, TimeSpan.Zero)).Leases);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await _orchestrator.Client.CompleteAsync(new CompleteRequest
        {
            TaskRunId = lease.TaskRunId,
            LeaseToken = lease.LeaseToken + 1_000_000,
            Outcome = TaskOutcome.Succeeded,
        }, OrchestratorHost.Bearer(token)));

        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    public async Task Failed_report_carries_the_error_and_retryability()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        var runId = await s.StartRunAsync();
        var lease = Assert.Single((await ClaimAsync(token, hostId, TimeSpan.Zero)).Leases);

        await _orchestrator.Client.CompleteAsync(new CompleteRequest
        {
            TaskRunId = lease.TaskRunId,
            LeaseToken = lease.LeaseToken,
            Outcome = TaskOutcome.Failed,
            Error = new TaskError { Type = "System.ArgumentException", Message = "bad region", Retryable = false },
        }, OrchestratorHost.Bearer(token));

        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("failed", row.Status);
        Assert.Contains("bad region", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_stream_is_stored_and_a_stale_chunk_is_failed_precondition()
    {
        var (s, token, hostId) = await RegisteredAsync(wf => wf.Task<Noop>("a"));
        await s.StartRunAsync();
        var lease = Assert.Single((await ClaimAsync(token, hostId, TimeSpan.Zero)).Leases);

        using (var call = _orchestrator.Client.AppendLogs(OrchestratorHost.Bearer(token)))
        {
            await call.RequestStream.WriteAsync(Chunk(lease, lease.LeaseToken, 1, 2));
            await call.RequestStream.WriteAsync(Chunk(lease, lease.LeaseToken, 3));
            await call.RequestStream.CompleteAsync();
            Assert.Equal(3, (await call).Accepted);
        }

        using var stale = _orchestrator.Client.AppendLogs(OrchestratorHost.Bearer(token));
        await stale.RequestStream.WriteAsync(Chunk(lease, lease.LeaseToken - 1, 4));
        await stale.RequestStream.CompleteAsync();
        var error = await Assert.ThrowsAsync<RpcException>(async () => await stale);
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(3L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM task_logs WHERE task_run_id = @id",
            ("id", Guid.Parse(lease.TaskRunId))));
    }

    private static LogChunk Chunk(TaskLease lease, long token, params int[] seqs) => new()
    {
        TaskRunId = lease.TaskRunId,
        Attempt = lease.Attempt,
        LeaseToken = token,
        Lines = { seqs.Select(seq => new LogLine { Seq = seq, Level = 2, Message = $"line {seq}", LoggedAt = Timestamp.FromDateTime(DateTime.UtcNow) }) },
    };

    private async Task<ClaimReply> ClaimAsync(string token, string hostId, TimeSpan maxWait, int freeSlots = 4) =>
        await _orchestrator.Client.ClaimAsync(
            new ClaimRequest { HostId = hostId, FreeSlots = freeSlots, MaxWait = Duration.FromTimeSpan(maxWait) },
            OrchestratorHost.Bearer(token));

    private async Task<(DispatchScenario Scenario, string Token, string HostId)> RegisteredAsync(
        Action<Abstractions.IWorkflowBuilder<NoParams>> define)
    {
        var s = await DispatchScenario.CreateAsync(pg, define);
        var (_, token) = await HostData.IssueCredentialAsync(pg, s.GroupId);
        var reply = await _orchestrator.RegisterAsync(token, HostData.NewName("host"), s.GroupName, hashes: [s.Hash]);
        return (s, token, reply.HostId);
    }
}
