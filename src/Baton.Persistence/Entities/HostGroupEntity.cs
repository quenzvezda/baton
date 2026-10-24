namespace Baton.Persistence.Entities;

/// <summary>Row of <c>host_groups</c>: a pool of agents with run/task limits and lease length.</summary>
public sealed class HostGroupEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary><c>null</c> = unlimited.</summary>
    public int? MaxConcurrentRuns { get; set; }

    /// <summary><c>null</c> = unlimited.</summary>
    public int? MaxConcurrentTasks { get; set; }

    public int LeaseSeconds { get; set; } = 60;
    public bool DispatchPaused { get; set; }
}
