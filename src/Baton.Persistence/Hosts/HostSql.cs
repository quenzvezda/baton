using System.Globalization;
using Baton.Domain.Hosts;

namespace Baton.Persistence.Hosts;

/// <summary>
/// SQL fragments for host status, shared by the inventory and dispatch (M3) so both agree on who is
/// Online (HST-03, design.md decisions 5 and 9). Heartbeat age is measured with Postgres <c>now()</c>.
/// </summary>
public static class HostSql
{
    private static readonly string OfflineAfterSeconds =
        HostLiveness.OfflineAfter.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>true</c> when the host's last heartbeat is older than the Offline threshold.</summary>
    public static string IsOffline(string alias) =>
        $"({alias}.last_heartbeat_at < now() - make_interval(secs => {OfflineAfterSeconds}))";

    /// <summary>
    /// The visible status: Offline when the heartbeat is stale, else Draining when an operator drained the
    /// host or the agent reported it is shutting down, else Online.
    /// </summary>
    public static string DerivedStatus(string alias) => $"""
        CASE WHEN {IsOffline(alias)} THEN '{HostStatus.Offline}'
             WHEN {alias}.drain_requested OR {alias}.status = '{HostStatus.Draining}' THEN '{HostStatus.Draining}'
             ELSE '{HostStatus.Online}' END
        """;

    /// <summary>Only Online hosts receive new work (HST-03, HST-04). Claims in M3 filter on this.</summary>
    public static string IsEligible(string alias) => $"(({DerivedStatus(alias)}) = '{HostStatus.Online}')";

    /// <summary>
    /// A credential may act for the host when its group <c>@credentialGroupId</c> is the host's group, or
    /// when it is the very credential <c>@credentialId</c> the host registered with (ADR-0013, M2 design.md
    /// decision 7), so a moved host keeps working with its old token. Matching the bound credential by id —
    /// not by group — keeps other tokens of the host's former group out (G1 security review M1); the
    /// caller's credential is known to be unrevoked because it passed the auth interceptor.
    /// </summary>
    public static string MayActFor(string alias) => $"""
        ({alias}.host_group_id = @credentialGroupId OR {alias}.credential_id = @credentialId)
        """;
}
