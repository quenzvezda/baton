using System.Collections.Concurrent;
using Baton.Agent.Protocol.V1;
using Google.Protobuf.WellKnownTypes;

namespace Baton.Agent.Execution;

/// <summary>
/// The <see cref="ILogger"/> handed to a handler (<c>TaskContext.Logger</c>, OBS-03): every line gets the
/// next sequence number and is queued for <see cref="TaskLogShipper"/>; it is also written to the agent's
/// own log under the category <c>Baton.Task</c>.
/// </summary>
public sealed class TaskLogBuffer(ILogger forward) : ILogger
{
    private readonly ConcurrentQueue<LogLine> _lines = new();
    private int _seq;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => forward.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (exception is not null)
            message = $"{message}{Environment.NewLine}{exception}";

        _lines.Enqueue(new LogLine
        {
            Seq = Interlocked.Increment(ref _seq),
            LoggedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Level = (int)logLevel,
            Message = message,
        });
        forward.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>Takes up to <paramref name="max"/> queued lines, oldest first.</summary>
    public List<LogLine> Take(int max)
    {
        var taken = new List<LogLine>();
        while (taken.Count < max && _lines.TryDequeue(out var line))
            taken.Add(line);
        return taken;
    }

    public bool IsEmpty => _lines.IsEmpty;
}
