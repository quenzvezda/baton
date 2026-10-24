# ADR-0008: Replica coordination with SKIP LOCKED and transaction-scoped advisory locks

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §3 (Availability), §6 "High availability", §7 "Operational notes", §8 "Orchestrator loops"

## Context

The orchestrator runs as 2+ replicas for 99.9 % availability. Some loops are safe to run everywhere
(each row is processed by whoever locks it first); others must run once per tick (admission ordering,
priority aging, limit reconciliation). We do not want ZooKeeper/etcd or a leader-election service.

## Decision

- Row-oriented loops (claims, trigger loop, reaper, retry promoter) use `FOR UPDATE SKIP LOCKED` and
  run on every replica.
- Once-per-tick loops derive from `SingletonLoop` and call `pg_try_advisory_xact_lock(<LockId>)` inside
  the tick transaction; replicas that fail to take it skip the tick. Admission takes one lock per group.
- Only **transaction-scoped** advisory locks are used: they work through PgBouncer in transaction mode
  and release on rollback/crash.
- Lock IDs are constants defined in one place.

## Consequences

**Positive**
- No extra coordination infrastructure; a crashed replica's work is picked up on the next tick.

**Negative / risks**
- Singleton loop latency is bounded by the tick interval, not instant failover → intervals are short.
- Lock-ID collisions → single registry of constants plus a unit test for uniqueness.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Session-level advisory locks / lease rows for leadership | Break through PgBouncer or need their own lease management. |
| External leader election (Kubernetes Lease, etcd) | Extra dependency; ties us to a runtime platform (see OQ-06). |
