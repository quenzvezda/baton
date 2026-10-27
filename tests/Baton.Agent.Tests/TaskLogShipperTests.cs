using Baton.Agent.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baton.Agent.Tests;

/// <summary>Task 4.3: handler log lines reach the orchestrator in order, with sequence numbers, fenced by the lease.</summary>
public sealed class TaskLogShipperTests : IAsyncLifetime
{
    private AgentHarness _agent = null!;

    public Task InitializeAsync()
    {
        _agent = AgentHarness.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _agent.DisposeAsync();

    [Fact]
    public async Task Lines_arrive_in_order_with_sequence_numbers_in_chunks()
    {
        var lease = _agent.Lease("write-logs");
        var buffer = new TaskLogBuffer(NullLogger.Instance);
        using var shipper = new TaskLogShipper(_agent.Client, lease, buffer, NullLogger.Instance);
        using var task = new RunningTask(lease);

        using var stop = new CancellationTokenSource();
        var pump = shipper.PumpAsync(stop.Token);
        await _agent.Executor.ExecuteAsync(task, buffer, CancellationToken.None);
        await stop.CancelAsync();
        await pump;
        await shipper.FlushAsync(CancellationToken.None);

        var chunks = _agent.Fake.LogChunks.ToList();
        Assert.True(chunks.Count >= 3, "1,200 lines go in chunks of at most 500.");
        Assert.All(chunks, c =>
        {
            Assert.Equal(lease.TaskRunId, c.TaskRunId);
            Assert.Equal(lease.LeaseToken, c.LeaseToken);
            Assert.Equal(1, c.Attempt);
            Assert.InRange(c.Lines.Count, 1, 500);
        });
        var lines = chunks.SelectMany(c => c.Lines).ToList();
        Assert.Equal(Enumerable.Range(1, 1200), lines.Select(l => l.Seq));
        Assert.Equal("line 1", lines[0].Message);
        Assert.Equal("line 1200", lines[^1].Message);
    }

    [Fact]
    public async Task Stale_lease_stops_shipping()
    {
        var lease = _agent.Lease("echo");
        var buffer = new TaskLogBuffer(NullLogger.Instance);
        using var shipper = new TaskLogShipper(_agent.Client, lease, buffer, NullLogger.Instance);
        _agent.Fake.Stale = true;
        buffer.Log(Microsoft.Extensions.Logging.LogLevel.Information, default, "first", null, (s, _) => s);
        await shipper.FlushAsync(CancellationToken.None);

        _agent.Fake.Stale = false;
        buffer.Log(Microsoft.Extensions.Logging.LogLevel.Information, default, "second", null, (s, _) => s);
        await shipper.FlushAsync(CancellationToken.None);

        Assert.Empty(_agent.Fake.LogChunks);
    }
}
