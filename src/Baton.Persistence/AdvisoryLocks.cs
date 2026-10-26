namespace Baton.Persistence;

/// <summary>
/// Every <c>pg_try_advisory_xact_lock</c> key used by Baton, in one place so they never collide
/// (ADR-0008). Transaction-scoped locks only: session locks break under PgBouncer transaction mode.
/// </summary>
public static class AdvisoryLocks
{
    /// <summary>High 32 bits spell "Bato", keeping Baton's keys apart from other users of the database.</summary>
    private const long Prefix = 0x4261_746F_0000_0000;

    /// <summary>Singleton <c>PartitionMaintenanceLoop</c> (ADR-0011).</summary>
    public const long PartitionMaintenance = Prefix + 1;

    /// <summary>Singleton <c>LimitReconcilerLoop</c> (M3 design.md decision 8).</summary>
    public const long LimitReconciler = Prefix + 2;
}
