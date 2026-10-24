namespace Baton.Abstractions;

/// <summary>Entry point handed to <see cref="IWorkflowDefinition.Build"/>. Implemented in Baton.Domain (M1).</summary>
public interface IWorkflowBuilder
{
    IWorkflowBuilder<TParams> For<TParams>();
}

public interface IWorkflowBuilder<TParams>
{
    /// <summary>Declares a task bound to a handler type. The key must be unique, kebab-case, at most 64 characters.</summary>
    ITaskBuilder Task<THandler>(string key) where THandler : class;
}

/// <summary>
/// Fluent options for one task node. Defaults: 3 attempts, 30 s initial delay, x2, max 10 min delay,
/// 1 h timeout, <see cref="TriggerRule.AllSuccess"/>. Platform bounds, checked at startup: at most
/// 10 attempts and a timeout of at most 24 h.
/// </summary>
public interface ITaskBuilder
{
    string Key { get; }

    ITaskBuilder DependsOn(params ITaskBuilder[] upstream);

    /// <summary><paramref name="maxAttempts"/> counts the first attempt; <paramref name="maxDelay"/> defaults to 10 min.</summary>
    ITaskBuilder Retry(int maxAttempts, TimeSpan initialDelay, double multiplier = 2.0, TimeSpan? maxDelay = null);

    /// <summary>Per attempt. Defaults to 1 h; at most 24 h.</summary>
    ITaskBuilder Timeout(TimeSpan timeout);

    ITaskBuilder When(TriggerRule rule);

    ITaskBuilder RequireLabels(params string[] labels);

    /// <summary>Added to the run priority and clamped to 0–100.</summary>
    ITaskBuilder PriorityOffset(int offset);
}
