using System.Reflection;
using Baton.Domain.Workflows;
using Microsoft.Extensions.Options;

namespace Baton.Agent.Worker;

/// <summary>
/// What the agent reports about its build when it registers (HST-02): its name, version and the graph
/// hashes of the definitions it carries, so dispatch only sends it tasks it can run.
/// </summary>
public sealed class AgentIdentity(IOptions<AgentOptions> options, WorkflowSet workflows)
{
    public string HostName { get; } = options.Value.HostName;

    public string Version { get; } =
        typeof(AgentIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AgentIdentity).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public IReadOnlyList<string> DefinitionHashes { get; } = [.. workflows.Workflows.Select(w => w.Hash).Distinct()];
}
