namespace Baton.Domain.Hosts;

/// <summary>Values of <c>hosts.status</c> and of the status derived from heartbeat age (design.md decision 5).</summary>
public static class HostStatus
{
    public const string Online = "online";
    public const string Draining = "draining";
    public const string Offline = "offline";
}
