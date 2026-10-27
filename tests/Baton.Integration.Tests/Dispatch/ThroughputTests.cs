using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Baton.Agent.Protocol.V1;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>
/// M3 task 5.2 and exit criterion: 50 claims/s for 60 s on one group without errors, through gRPC against
/// Testcontainers Postgres. Excluded from CI (<c>Category=Load</c>); run it by hand and attach the report:
/// <c>dotnet test tests/Baton.Integration.Tests --filter Category=Load --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ThroughputTests(PostgresFixture pg, ITestOutputHelper output)
{
    private const int Agents = 10;
    private const int ClaimsPerSecond = 50;
    private static readonly TimeSpan Duration_ = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait("Category", "Load")]
    public async Task Fifty_claims_per_second_for_60_seconds_on_one_group_without_errors()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg);
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var tasks = (int)(ClaimsPerSecond * Duration_.TotalSeconds * 1.2);
        await HostData.ExecuteAsync(pg, """
            INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind, idempotency_key, started_at)
            SELECT gen_random_uuid(), @definitionId, 'running', 50, @groupId, '{}', 'manual', gen_random_uuid()::text, now()
            FROM generate_series(1, @tasks);
            INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, max_attempts, timeout,
                                   priority, effective_priority, host_group_id, definition_hash, queued_at)
            SELECT gen_random_uuid(), r.id, 'a', 'queued', 'all_success', 0, 3, interval '1 hour', 50, 50, @groupId, @hash, now()
            FROM workflow_runs r WHERE r.host_group_id = @groupId;
            """, ("definitionId", s.DefinitionId), ("groupId", s.GroupId), ("tasks", tasks), ("hash", s.Hash));
        var (_, token) = await HostData.IssueCredentialAsync(pg, s.GroupId);
        var headers = OrchestratorHost.Bearer(token);
        var hosts = new List<string>();
        for (var i = 0; i < Agents; i++)
            hosts.Add((await orchestrator.RegisterAsync(token, HostData.NewName("load"), s.GroupName, slots: 10, hashes: [s.Hash])).HostId);

        var claimLatency = new ConcurrentBag<double>();
        var completeLatency = new ConcurrentBag<double>();
        var errors = new ConcurrentQueue<string>();
        var empty = 0;
        var interval = TimeSpan.FromSeconds((double)Agents / ClaimsPerSecond);
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(hosts.Select(hostId => Task.Run(async () =>
        {
            // Like a real agent: a heartbeat every 5 s keeps the host Online for the whole minute.
            var heartbeat = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(interval);
            while (clock.Elapsed < Duration_ && await timer.WaitForNextTickAsync())
            {
                if (heartbeat.Elapsed > TimeSpan.FromSeconds(5))
                {
                    await orchestrator.HeartbeatAsync(token, hostId);
                    heartbeat.Restart();
                }

                try
                {
                    var call = Stopwatch.StartNew();
                    var reply = await orchestrator.Client.ClaimAsync(
                        new ClaimRequest { HostId = hostId, FreeSlots = 1, MaxWait = Duration.FromTimeSpan(TimeSpan.Zero) }, headers);
                    claimLatency.Add(call.Elapsed.TotalMilliseconds);
                    if (reply.Leases.Count == 0)
                    {
                        Interlocked.Increment(ref empty);
                        continue;
                    }

                    var lease = reply.Leases[0];
                    call.Restart();
                    await orchestrator.Client.CompleteAsync(new CompleteRequest
                    {
                        TaskRunId = lease.TaskRunId,
                        LeaseToken = lease.LeaseToken,
                        Outcome = TaskOutcome.Succeeded,
                        OutputJson = Google.Protobuf.ByteString.CopyFromUtf8("1"),
                    }, headers);
                    completeLatency.Add(call.Elapsed.TotalMilliseconds);
                }
                catch (RpcException ex)
                {
                    errors.Enqueue($"{ex.StatusCode}: {ex.Status.Detail}");
                }
            }
        })));
        var elapsed = clock.Elapsed;

        var report = Report(claimLatency.ToArray(), completeLatency.ToArray(), errors.Count, empty, elapsed);
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "m3-throughput-report.md"), report);

        Assert.Empty(errors);
        Assert.Equal(0, empty);
        Assert.True(claimLatency.Count >= ClaimsPerSecond * Duration_.TotalSeconds * 0.95,
            $"Only {claimLatency.Count} claims in {elapsed}.");
    }

    private static string Report(double[] claims, double[] completes, int errors, int empty, TimeSpan elapsed)
    {
        static string P(double[] values, double p) =>
            values.Length == 0 ? "-" : values.Order().ElementAt((int)Math.Ceiling(values.Length * p) - 1).ToString("0.0", CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"M3 throughput smoke: {Agents} agents, one group, target {ClaimsPerSecond} claims/s for {Duration_.TotalSeconds:0} s");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("| --- | --- |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Duration | {elapsed.TotalSeconds:0.0} s |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Claims (each leased one task) | {claims.Length - empty} |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Claim rate | {claims.Length / elapsed.TotalSeconds:0.0} /s |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Empty claims | {empty} |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Errors | {errors} |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Claim latency p50 / p95 / p99 | {P(claims, 0.5)} / {P(claims, 0.95)} / {P(claims, 0.99)} ms |");
        text.AppendLine(CultureInfo.InvariantCulture, $"| Complete latency p50 / p95 / p99 | {P(completes, 0.5)} / {P(completes, 0.95)} / {P(completes, 0.99)} ms |");
        return text.ToString();
    }
}
