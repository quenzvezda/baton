namespace Baton.Agent.Worker;

/// <summary>
/// Agent settings, bound from <c>Agent</c>. Keep <see cref="Token"/> in user secrets or the environment
/// (<c>Agent__Token</c>), never in appsettings.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Host name, unique across the platform; empty means the machine name.</summary>
    public string Name { get; set; } = "";

    public string HostGroup { get; set; } = "";

    public string[] Labels { get; set; } = [];

    public int CapacitySlots { get; set; } = 4;

    public Uri? OrchestratorUrl { get; set; }

    /// <summary>Credential of <see cref="HostGroup"/>, issued by an Admin (ADR-0013).</summary>
    public string Token { get; set; } = "";

    /// <summary>Allows a plaintext orchestrator URL on a non-loopback host. Development only (ADR-0013).</summary>
    public bool AllowInsecureChannel { get; set; }

    /// <summary>Used until the orchestrator's Register reply sets the interval.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>Claim long-poll (M3).</summary>
    public int ClaimLongPollSeconds { get; set; } = 20;

    public string HostName => string.IsNullOrWhiteSpace(Name) ? Environment.MachineName.ToLowerInvariant() : Name;

    /// <summary>Problems with these settings, empty when the agent can start.</summary>
    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(HostGroup))
            yield return "Agent:HostGroup is required.";
        if (CapacitySlots is < 1 or > 1024)
            yield return "Agent:CapacitySlots must be between 1 and 1024.";
        if (OrchestratorUrl is null)
            yield return "Agent:OrchestratorUrl is required.";
        else if (OrchestratorUrl.Scheme != Uri.UriSchemeHttps && !OrchestratorUrl.IsLoopback && !AllowInsecureChannel)
            yield return "Agent:OrchestratorUrl must be https unless it is loopback or Agent:AllowInsecureChannel is set (ADR-0013).";
        if (string.IsNullOrWhiteSpace(Token))
            yield return "Agent:Token is required; set it with user secrets or the Agent__Token environment variable.";
        if (HeartbeatIntervalSeconds is < 1 or > 60)
            yield return "Agent:HeartbeatIntervalSeconds must be between 1 and 60.";
    }
}
