namespace Baton.Domain.Notifications;

/// <summary>
/// OBS-05 (add-operability-and-insight design.md decision 19): a delivery is attempted up to
/// <see cref="MaxAttempts"/> times, waiting about 1, 5, 15 and 30 minutes between attempts, ±10 % jitter so a
/// burst of failures does not retry in lockstep.
/// </summary>
public static class NotificationSchedule
{
    public const int MaxAttempts = 5;

    public const double MaxJitter = 0.1;

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    /// <summary>Whether a delivery that has failed <paramref name="attempts"/> times is given up.</summary>
    public static bool IsFinal(int attempts) => attempts >= MaxAttempts;

    /// <param name="attempt">1-based number of the attempt that just failed, below <see cref="MaxAttempts"/>.</param>
    /// <param name="jitter">Optional source in [0,1); defaults to <see cref="Random.Shared"/>. Injected for tests.</param>
    public static TimeSpan Delay(int attempt, Func<double>? jitter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(attempt, MaxAttempts);

        var j = ((jitter ?? Random.Shared.NextDouble)() * 2 - 1) * MaxJitter;
        return Delays[attempt - 1] * (1 + j);
    }
}
