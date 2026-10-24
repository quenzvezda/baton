# ADR-0009: Concurrency limits as transactional counter rows

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §6 "Limits", §7 `concurrency_limits`, ORC-02, HST-01, AUT-06

## Context

Limits exist per automation, workflow, host group (runs and tasks) and later per resource pool.
They must be enforced exactly across orchestrator replicas, and released when work finishes —
including on crash paths.

## Decision

- One row per limit key in `concurrency_limits (key, max, in_use)` with `CHECK (in_use >= 0)`.
  Keys: `automation:{id}`, `workflow:{key}`, `group-runs:{id}`, `group-tasks:{id}`, later `pool:{name}`.
- Run-level limits are reserved at admission; task-level limits at claim — in the **same transaction**
  as the state change they guard. Held keys are stored on the run (`held_limits`) for release.
- Rows are locked in key order to prevent deadlocks between replicas.
- Admission skips a run that does not fit instead of blocking lower-priority runs that do.
- A reconciler recomputes `in_use` from live rows every 5 minutes as a safety net.

## Consequences

**Positive**
- Exact enforcement with plain row locks; trivially observable in SQL.

**Negative / risks**
- The `group-tasks` row serializes claims within a group for a few ms → acceptable at target load;
  measure at G1. Groups without a task limit skip the row entirely.
- Counter drift on bugs → reconciler + alert on correction.

## Alternatives considered

| Option | Why not |
| --- | --- |
| `COUNT(*)` of active rows at decision time | Racy without serializable isolation; expensive at scale. |
| In-memory semaphores | Not shared across replicas; lost on crash. |
