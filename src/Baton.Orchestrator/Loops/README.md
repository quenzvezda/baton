# Orchestrator loops

Each loop is a `BackgroundService` (design spec §8, ADR-0008).

| Loop | Interval | Coordination | Milestone |
| --- | --- | --- | --- |
| TriggerLoop | 1 s | `SKIP LOCKED` | M4 |
| AdmissionLoop | 1 s | advisory lock per group | M4 |
| ReaperLoop | 5 s | `SKIP LOCKED` | M3 |
| RetryPromoterLoop | 1 s | `SKIP LOCKED` | M3 |
| AgingLoop | 30 s | singleton (advisory lock) | M6 |
| LimitReconcilerLoop | 5 min | singleton (advisory lock) | M4 |
| PartitionMaintenanceLoop | 1 day | singleton (advisory lock) | M0 |

Singleton loops derive from `SingletonLoop` and take `pg_try_advisory_xact_lock(LockId)`
inside the tick transaction — never session-level locks (PgBouncer, see ADR-0008).
Lock IDs are constants in `Baton.Persistence/AdvisoryLocks.cs` to avoid collisions.
