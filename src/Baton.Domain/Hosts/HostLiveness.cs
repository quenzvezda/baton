namespace Baton.Domain.Hosts;

/// <summary>Heartbeat cadence and the Offline threshold (HST-03). Ages are measured with Postgres <c>now()</c>.</summary>
public static class HostLiveness
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>A host whose last heartbeat is older than this is Offline and receives no new work.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(30);
}
