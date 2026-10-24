namespace Baton.Abstractions;

/// <summary>Throw from a handler to fail the task at once without using its remaining retries (WF-08).</summary>
public sealed class NonRetryableException : Exception
{
    public NonRetryableException(string message) : base(message) { }

    public NonRetryableException(string message, Exception innerException) : base(message, innerException) { }
}
