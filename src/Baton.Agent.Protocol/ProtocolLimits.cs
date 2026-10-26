namespace Baton.Agent.Protocol.V1;

/// <summary>Limits both ends of <c>AgentService</c> configure the same way.</summary>
public static class ProtocolLimits
{
    /// <summary>
    /// Largest gRPC message either side sends or accepts: a claim carries the outputs of every succeeded
    /// ancestor inline, up to 256 KB each (M3 design.md decision 6).
    /// </summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;
}
