# Design

## Context

See proposal.md. Constraints: ADR-0011 (monthly partitions, 90-day retention, alert when fewer than 2
future partitions), ADR-0008 (singleton loops under transaction-scoped advisory locks), ADR-0003
(Postgres only), "Time comes from Postgres `now()`". Scale from OQ-01 (2026-11-14): ~10k task runs and
~1k runs per day, so 90 days is roughly 90k runs and 900k tasks.

## Goals / Non-Goals

**Goals:** bounded storage at the OQ-01 scale; a stalled maintenance loop is visible on `/healthz` long
before inserts fail; corrections are countable by an alert rule.

**Non-Goals:** alert rules and dashboards themselves (the G1 runbooks name the signals; OBS-04/OBS-06 in
M7 build the rest); archiving history before dropping it.

## Decisions

1. **Retention runs inside `PartitionMaintenanceLoop`'s daily tick**, after `ensure_history_partitions`,
   in the same singleton transaction (one transaction per housekeeping step is enough; nothing else
   reads the dropped rows' state). SQL lives in `Baton.Persistence/History/HistoryRetention.cs` as
   hand-written SQL next to the queue code's style; no new migration and no DB function, so the schema
   test is unchanged.
2. **Which partitions are dropped.** Children of `task_attempts`, `task_logs`, `run_events` named
   `<parent>_yYYYYmMM` (the names `ensure_history_partitions` creates) whose month end, in UTC, is at or
   before `now() - 90 days`. Partitions with any other name are never touched. History is therefore
   kept between 90 and ~121 days — retention is "at least 90 days", which is what ADR-0011 needs.
3. **Lock safety.** Dropping a partition takes an `ACCESS EXCLUSIVE` lock on its parent. The tick sets
   `lock_timeout = 5s` (`SET LOCAL`) so a long reader cannot make writers queue behind the drop; on
   timeout the tick fails, is logged by `SingletonLoop`, and retries the next day.
4. **Which runs are purged.** `workflow_runs` with a final status (`succeeded`, `failed`, `cancelled`,
   `timed_out`) and `finished_at < now() - 90 days`, oldest first, at most 5,000 per tick;
   `task_runs` go with them through the existing `ON DELETE CASCADE`. History rows of purged runs that
   sit in a not-yet-dropped partition disappear with that partition later. The monitoring API already
   returns 404 for missing runs. No index on `finished_at`: a daily scan of ~90k rows is cheap at the
   OQ-01 scale; add a partial index if that changes. 5,000 per tick covers 5× the expected daily volume.
5. **Partition health.** A second health check, `history-partitions`, counts per history table the
   partitions whose month starts after the current UTC month; fewer than 2 for any table ⇒
   `Degraded` with the table names in the description. Degraded keeps HTTP 200 (default ASP.NET Core
   mapping) so a liveness probe does not restart a working orchestrator; `/healthz` reports
   `"status": "degraded"` instead of the current `"unhealthy"` for that case.
6. **Metrics.** A `Baton.Orchestrator` meter (through `IMeterFactory`) with counters
   `baton.limits.corrections` (tag `limit.kind`: the key prefix such as `group-tasks`, `automation` —
   never the full key, to keep cardinality low), `baton.history.partitions_dropped` and
   `baton.history.runs_purged`. `BatonTelemetry` adds `AddMeter("Baton.*")` so they export with the
   rest when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. `System.Diagnostics.Metrics` is in the BCL: no new package.

## Risks / Trade-offs

- [Drop blocked by a long query] → `lock_timeout`, retried next day; runbook "Postgres outage" covers it.
- [Backlog after long downtime] → 5,000 runs per tick drains ~5 days of backlog per day.
- [Renamed partitions] → never dropped; documented here so manual partitions use the same names.
