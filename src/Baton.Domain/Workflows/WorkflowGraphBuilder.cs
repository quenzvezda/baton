using Baton.Abstractions;
using Baton.Domain.Retries;

namespace Baton.Domain.Workflows;

/// <summary>
/// Records what a definition declares. It accepts anything, including duplicate or malformed keys, so
/// that <see cref="WorkflowValidator"/> can report every problem at once.
/// </summary>
internal sealed class WorkflowGraphBuilder(Type paramsType) : IWorkflowBuilder
{
    private readonly List<TaskBuilder> _tasks = [];

    public IWorkflowBuilder<TParams> For<TParams>()
    {
        if (typeof(TParams) != paramsType)
        {
            throw new InvalidOperationException(
                $"Build declares params type '{typeof(TParams)}' but ParamsType is '{paramsType}'.");
        }

        return new Typed<TParams>(this);
    }

    public IReadOnlyList<TaskNode> ToNodes() => [.. _tasks.Select(t => t.ToNode())];

    private TaskBuilder Add(string key, Type handlerType)
    {
        var task = new TaskBuilder(key ?? "", handlerType);
        _tasks.Add(task);
        return task;
    }

    private sealed class Typed<TParams>(WorkflowGraphBuilder owner) : IWorkflowBuilder<TParams>
    {
        public ITaskBuilder Task<THandler>(string key) where THandler : class => owner.Add(key, typeof(THandler));
    }

    private sealed class TaskBuilder(string key, Type handlerType) : ITaskBuilder
    {
        private readonly List<string> _dependsOn = [];
        private readonly List<string> _labels = [];
        private RetryPolicy _retry = TaskDefaults.Retry;
        private TimeSpan _timeout = TaskDefaults.Timeout;
        private TriggerRule _rule = TaskDefaults.Rule;
        private int _priorityOffset = TaskDefaults.PriorityOffset;

        public string Key => key;

        public ITaskBuilder DependsOn(params ITaskBuilder[] upstream)
        {
            ArgumentNullException.ThrowIfNull(upstream);
            _dependsOn.AddRange(upstream.Select(u => u.Key));
            return this;
        }

        public ITaskBuilder Retry(int maxAttempts, TimeSpan initialDelay, double multiplier = 2.0, TimeSpan? maxDelay = null)
        {
            _retry = new RetryPolicy(maxAttempts, initialDelay, multiplier, maxDelay ?? TaskDefaults.Retry.MaxDelay);
            return this;
        }

        public ITaskBuilder Timeout(TimeSpan timeout)
        {
            _timeout = timeout;
            return this;
        }

        public ITaskBuilder When(TriggerRule rule)
        {
            _rule = rule;
            return this;
        }

        public ITaskBuilder RequireLabels(params string[] labels)
        {
            ArgumentNullException.ThrowIfNull(labels);
            _labels.AddRange(labels);
            return this;
        }

        public ITaskBuilder PriorityOffset(int offset)
        {
            _priorityOffset = offset;
            return this;
        }

        public TaskNode ToNode() =>
            new(key, handlerType, [.. _dependsOn], _retry, _timeout, _rule, [.. _labels], _priorityOffset);
    }
}
