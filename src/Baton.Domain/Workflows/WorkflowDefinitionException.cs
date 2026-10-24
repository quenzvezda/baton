namespace Baton.Domain.Workflows;

/// <summary>
/// One or more workflow definitions are invalid or conflict with what is registered. Thrown at
/// startup, so the process refuses to start (WF-03, WF-04). Each error starts with <c>key@version</c>.
/// </summary>
public sealed class WorkflowDefinitionException : Exception
{
    public WorkflowDefinitionException(IReadOnlyList<string> errors)
        : this(errors, innerException: null)
    {
    }

    public WorkflowDefinitionException(IReadOnlyList<string> errors, Exception? innerException)
        : base(Format(errors), innerException)
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    private static string Format(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return "Invalid workflow definitions:" + string.Concat(errors.Select(e => $"{Environment.NewLine}  - {e}"));
    }
}
