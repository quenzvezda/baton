namespace Baton.Orchestrator.Dispatch;

/// <summary>Dispatch timings, bound from <c>Dispatch</c>. The defaults are the design's (design spec §6, §8); tests shorten them.</summary>
public sealed class DispatchOptions
{
    public const string Section = "Dispatch";

    /// <summary>Claim long-poll when the agent asks for none (ClaimRequest.max_wait).</summary>
    public TimeSpan DefaultClaimWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Upper bound on an agent's requested long-poll.</summary>
    public TimeSpan MaxClaimWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A waiting claim re-checks this often even without a notification (NOTIFY is a hint only).</summary>
    public TimeSpan ClaimRecheck { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryPromoterInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan LimitReconcilerInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Due cron triggers (M4, NFR-TriggerAccuracy: a run within 5 s of the fire time).</summary>
    public TimeSpan TriggerInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan AdmissionInterval { get; set; } = TimeSpan.FromSeconds(1);
}
