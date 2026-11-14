namespace Baton.Domain.Automations;

/// <summary>What a cron trigger does with fire times it missed during downtime (AUT-08, design spec §5).</summary>
public enum MisfirePolicy
{
    /// <summary>One run for the most recent missed time (CatchUp with a limit of 1).</summary>
    FireOnce,

    /// <summary>No run for a missed time.</summary>
    Skip,

    /// <summary>Runs for the most recent missed times, up to the trigger's catch-up limit.</summary>
    CatchUp,
}

public static class MisfirePolicies
{
    public const int DefaultCatchUpLimit = 3;
    public const int MinCatchUpLimit = 1;
    public const int MaxCatchUpLimit = 10;

    /// <summary>A fire time more than this late is missed; up to this late it fires as usual (design.md decision 2).</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(60);

    public static string ToDb(MisfirePolicy policy) => policy switch
    {
        MisfirePolicy.FireOnce => "fire_once",
        MisfirePolicy.Skip => "skip",
        MisfirePolicy.CatchUp => "catch_up",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static bool TryParse(string? value, out MisfirePolicy policy)
    {
        (var known, policy) = value switch
        {
            "fire_once" => (true, MisfirePolicy.FireOnce),
            "skip" => (true, MisfirePolicy.Skip),
            "catch_up" => (true, MisfirePolicy.CatchUp),
            _ => (false, MisfirePolicy.FireOnce),
        };
        return known;
    }

    public static MisfirePolicy Parse(string value) =>
        TryParse(value, out var policy) ? policy : throw new ArgumentOutOfRangeException(nameof(value), value, null);

    /// <summary>How many of the most recent missed times fire: 1, 0 or the catch-up limit.</summary>
    public static int MissedToFire(MisfirePolicy policy, int? catchUpLimit) => policy switch
    {
        MisfirePolicy.FireOnce => 1,
        MisfirePolicy.Skip => 0,
        MisfirePolicy.CatchUp => Math.Clamp(catchUpLimit ?? DefaultCatchUpLimit, MinCatchUpLimit, MaxCatchUpLimit),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };
}
