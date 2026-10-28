using Cronos;

namespace Baton.Domain.Automations;

/// <summary>
/// A 5-field cron expression in an IANA time zone (AUT-04, design.md decision 11), evaluated with Cronos.
/// Fire times are UTC instants. On DST transitions Cronos semantics apply: a time skipped by a forward
/// jump fires at the first valid instant after the jump, and a time repeated by a backward jump fires once.
/// </summary>
public sealed class CronSchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _zone;

    private CronSchedule(CronExpression expression, TimeZoneInfo zone)
    {
        _expression = expression;
        _zone = zone;
    }

    /// <returns><c>null</c>, with the reasons keyed <c>cronExpression</c> and <c>timeZone</c> in <paramref name="errors"/>.</returns>
    public static CronSchedule? TryCreate(string? expression, string? timeZone, out Dictionary<string, string> errors)
    {
        errors = new Dictionary<string, string>(StringComparer.Ordinal);

        CronExpression? cron = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            errors["cronExpression"] = "Required for a cron trigger.";
        }
        else
        {
            try
            {
                cron = CronExpression.Parse(expression.Trim(), CronFormat.Standard);
            }
            catch (CronFormatException ex)
            {
                errors["cronExpression"] = $"Not a 5-field cron expression: {ex.Message}";
            }
        }

        TimeZoneInfo? zone = null;
        if (string.IsNullOrWhiteSpace(timeZone))
            errors["timeZone"] = "Required for a cron trigger: an IANA time zone such as 'Asia/Singapore' or 'UTC'.";
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out zone) || !zone.HasIanaId)
            errors["timeZone"] = $"'{timeZone}' is not an IANA time zone.";

        return cron is not null && zone is not null && errors.Count == 0 ? new CronSchedule(cron, zone) : null;
    }

    /// <summary>For values validated on save; throws when the expression or the zone is invalid.</summary>
    public static CronSchedule Create(string expression, string timeZone) =>
        TryCreate(expression, timeZone, out var errors)
        ?? throw new ArgumentException(string.Join(" ", errors.Values));

    /// <summary>
    /// The first fire time strictly after <paramref name="after"/>, or <c>null</c> when the expression never
    /// fires again. After downtime pass the database's <c>now()</c>: v1 fires a missed time once and then
    /// moves to the next future time (FireOnce, design.md "Next fire").
    /// </summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after) =>
        _expression.GetNextOccurrence(after, _zone, inclusive: false)?.ToUniversalTime();
}
