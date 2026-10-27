namespace Baton.Domain.Hosts;

/// <summary>Heartbeat cadence and the Offline threshold (HST-03). Ages are measured with Postgres <c>now()</c>.</summary>
public static class HostLiveness
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The interval a host is told at registration (M3 design.md decision 2): <c>min(10 s, lease / 6)</c>,
    /// so any lease tolerates five missed heartbeats.
    /// </summary>
    public static TimeSpan HeartbeatIntervalFor(TimeSpan lease) =>
        lease / 6 < HeartbeatInterval ? lease / 6 : HeartbeatInterval;

    /// <summary>A host whose last heartbeat is older than this is Offline and receives no new work.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(30);
}
