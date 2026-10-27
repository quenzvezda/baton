using System.Buffers.Binary;

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

    /// <summary>Keys with the low word's top bit set belong to per-group admission; the constants above stay below it.</summary>
    private const long AdmissionSpace = Prefix + 0x8000_0000;

    /// <summary>
    /// Admission of one host group (ADR-0008). Two groups may share a key (31 bits folded from the id); that
    /// only serializes their passes.
    /// </summary>
    public static long Admission(Guid groupId)
    {
        Span<byte> bytes = stackalloc byte[16];
        groupId.TryWriteBytes(bytes);
        var folded = BinaryPrimitives.ReadUInt32LittleEndian(bytes) ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])
            ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        return AdmissionSpace + (folded & 0x7FFF_FFFF);
    }
}
