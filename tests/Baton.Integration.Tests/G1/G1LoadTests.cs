using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// Gate G1 Performance (OQ-01 profile, 2026-11-14): 20 dispatches/s sustained for 15 minutes on two orchestrator
/// replicas, p95 Queued→Leased under 2 s with free slots, cron runs created within 5 s. Runs by hand
/// (<c>--filter Category=G1</c>); <c>BATON_G1_LOAD_MINUTES</c> shortens it for a rehearsal.
/// </summary>
public sealed class G1LoadTests(ITestOutputHelper output)
{
    private const int Orchestrators = 2;
    private const int Agents = 6;
    private const int Slots = 4;
    private const int TasksPerRun = 4;
    private const double DispatchesPerSecond = 20;

    [Fact]
    [Trait("Category", "G1")]
    public async Task Twenty_dispatches_per_second_for_fifteen_minutes_meets_the_latency_targets()
    {
        var minutes = double.Parse(Environment.GetEnvironmentVariable("BATON_G1_LOAD_MINUTES") ?? "15", CultureInfo.InvariantCulture);
        G1Executions.Reset();
        await using var rig = await G1Rig.StartAsync(Orchestrators, Agents, Slots, leaseSeconds: 60, profile: "load");
        var timeline = new G1Rig.Timeline();

        await rig.StartCronAsync();
        timeline.Mark($"producer starts: {DispatchesPerSecond / TasksPerRun} runs/s × {TasksPerRun} tasks; cron every minute");
        using (var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes)))
            await rig.ProduceAsync(DispatchesPerSecond / TasksPerRun, stop.Token);
        timeline.Mark("producer stops");
        await rig.StopCronAsync();
        await rig.DrainAsync(TimeSpan.FromMinutes(2));
        timeline.Mark("every run final");

        var m = await rig.MeasureAsync();
        var report = Report(minutes, m, timeline);
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "g1-load-report.md"), report);

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.True(m.Accepted >= DispatchesPerSecond / TasksPerRun * minutes * 60 * 0.95, $"Only {m.Accepted} runs accepted.");
        Assert.True(m.RateAvg >= DispatchesPerSecond * 0.9, $"Average dispatch rate {m.RateAvg:0.0}/s.");
        Assert.True(m.LatencyP95 < 2, $"p95 Queued→Leased {m.LatencyP95:0.000} s.");
        Assert.True(m.CronRuns >= (int)minutes - 1, $"Only {m.CronRuns} cron runs.");
        Assert.True(m.CronMaxLateness < 5, $"A cron run was created {m.CronMaxLateness:0.000} s late.");
        Assert.Equal(0, m.LeaseOverlaps);
        Assert.Equal(0, m.DoubleSuccesses);
    }

    private static string Report(double minutes, G1Measurements m, G1Rig.Timeline timeline)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(c, $"G1 load: {Orchestrators} orchestrator replicas, {Agents} agents × {Slots} slots, target {DispatchesPerSecond} dispatches/s for {minutes:0.#} min");
        text.AppendLine();
        text.Append(m.Table());
        text.AppendLine();
        text.AppendLine("| At | Event |");
        text.AppendLine("| --- | --- |");
        text.Append(timeline);
        return text.ToString();
    }
}
