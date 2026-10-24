namespace Baton.Abstractions;

/// <summary>When a task runs once all upstream tasks are final (WF-06).</summary>
public enum TriggerRule
{
    /// <summary>Every upstream task succeeded (default). Otherwise Skipped.</summary>
    AllSuccess = 0,

    /// <summary>Always runs once upstream is final.</summary>
    AllDone = 1,

    /// <summary>At least one upstream task failed. Otherwise Skipped.</summary>
    OneFailed = 2,
}
