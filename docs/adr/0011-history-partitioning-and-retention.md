# ADR-0011: Monthly partitions and 90-day retention for history

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §3 (Retention), §7 "Operational notes"

## Context

`task_attempts`, `task_logs` and `run_events` grow with every run; deleting old rows row-by-row bloats
tables and loads autovacuum. Retention target: 90 days.

## Decision

- Those three tables are `PARTITION BY RANGE` on their timestamp, one partition per month, created
  ahead of time by `pg_partman` or a small scheduled job (decided in M0).
- Partitions older than 90 days are dropped. Finished `workflow_runs` and their `task_runs` are purged
  on the same schedule.

## Consequences

**Positive**
- Retention is an O(1) `DROP TABLE` per month; predictable vacuum load.

**Negative / risks**
- Primary keys must include the partition key → reflected in the schema.
- A missing future partition makes inserts fail → create 3 months ahead, alert when fewer than 2 exist.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Batched `DELETE` | Bloat and vacuum pressure on the busiest tables. |
| Archive to object storage | Not required for v1; can be added before the drop step. |
