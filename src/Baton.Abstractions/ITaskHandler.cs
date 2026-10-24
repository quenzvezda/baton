namespace Baton.Abstractions;

/// <summary>
/// One task's implementation. Resolved from DI per attempt. Tasks run at least once,
/// so handlers MUST be idempotent — use <see cref="TaskContext{TParams}.IdempotencyKey"/>.
/// </summary>
public interface ITaskHandler<TParams, TOutput>
{
    Task<TOutput> ExecuteAsync(TaskContext<TParams> ctx, CancellationToken ct);
}
