# ADR-0003: PostgreSQL as the single source of truth and work queue

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §1, §3 (Durability, Throughput), §4, §7

## Context

Baton must never lose an accepted run, must keep DAG state, the task queue, leases and concurrency
limits consistent with each other, and targets ~100k task runs/day with peaks of 50 dispatches/s.
A separate broker would split state across two systems and force distributed coordination
(outbox, dual writes, reconciliation) on every transition.

## Decision

PostgreSQL 16+ is the **only** stateful dependency. It holds configuration, run state, the task queue
(`task_runs` is both DAG state and queue), leases and limits. Every state transition — claim,
complete + DAG advance + limit release, reap — is a single Postgres transaction.
`LISTEN/NOTIFY` wakes waiting claims; notifications are hints only, backed by a 2 s re-check.

## Consequences

**Positive**
- Atomicity across queue, DAG and limits for free; no dual-write problem.
- One system to operate, back up and reason about; agents hold no state the orchestrator needs.

**Negative / risks**
- Postgres becomes the throughput ceiling → partial indexes, `SKIP LOCKED`, batched claims,
  fillfactor 80 + aggressive autovacuum on `task_runs`; load-test at Gate G1.
- `LISTEN` needs a dedicated, non-pooled connection (PgBouncer transaction mode breaks it).
- Availability depends on Postgres HA (primary + streaming replica).

## Alternatives considered

| Option | Why not |
| --- | --- |
| RabbitMQ/Kafka + Postgres | Two sources of truth; lease/fencing/limits become distributed problems. |
| Redis queues | Weaker durability; same dual-write problem. |
| Temporal .NET SDK | Strong engine, but extra cluster to run and no host-group routing model out of the box. |
| Hangfire / Elsa | Cover parts (jobs / workflows) but not DAG + host-group routing with fenced leases on Postgres alone. |
