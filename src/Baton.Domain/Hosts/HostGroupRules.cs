namespace Baton.Domain.Hosts;

/// <summary>Validation of host-group settings (HST-01, design.md "Implementation notes").</summary>
public static class HostGroupRules
{
    public const int MaxNameLength = 63;
    public const int DefaultLeaseSeconds = 60;
    public const int MinLeaseSeconds = 30;
    public const int MaxLeaseSeconds = 3600;

    /// <summary>1–63 characters of <c>[a-z0-9-]</c>, starting with a letter or digit; agents refer to groups by name.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && name[0] != '-'
        && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>Errors keyed by camelCase field name, empty when the settings are valid.</summary>
    public static Dictionary<string, string[]> Validate(string? name, int? maxConcurrentRuns, int? maxConcurrentTasks, int leaseSeconds)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!IsValidName(name))
            errors["name"] = [$"Use 1–{MaxNameLength} lowercase letters, digits or '-', starting with a letter or digit."];
        if (maxConcurrentRuns is < 1)
            errors["maxConcurrentRuns"] = ["Must be at least 1, or absent for unlimited."];
        if (maxConcurrentTasks is < 1)
            errors["maxConcurrentTasks"] = ["Must be at least 1, or absent for unlimited."];
        if (leaseSeconds is < MinLeaseSeconds or > MaxLeaseSeconds)
            errors["leaseSeconds"] = [$"Must be between {MinLeaseSeconds} and {MaxLeaseSeconds}."];
        return errors;
    }

    /// <summary>Key of the group's run limit row in <c>concurrency_limits</c> (ADR-0009).</summary>
    public static string RunsLimitKey(Guid groupId) => $"group-runs:{groupId}";

    /// <summary>Key of the group's task limit row in <c>concurrency_limits</c> (ADR-0009).</summary>
    public static string TasksLimitKey(Guid groupId) => $"group-tasks:{groupId}";
}
