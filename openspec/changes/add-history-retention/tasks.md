# Tasks

## 1. Retention

- [x] 1.1 Drop history partitions whose month ended more than 90 days ago, with `lock_timeout`; verify `Partitions_whose_month_ended_over_90_days_ago_are_dropped` and `Partition_still_inside_the_window_is_kept` (Testcontainers)
- [x] 1.2 Purge finished runs older than 90 days with their tasks, never active runs; verify `Finished_runs_older_than_90_days_are_purged_with_their_tasks` and `Active_runs_are_never_purged`
- [x] 1.3 Call both from `PartitionMaintenanceLoop`'s tick and count drops/purges; verify `Partition_maintenance_tick_runs_against_the_migrated_schema` still passes and the loop test drops an expired partition

## 2. Signals

- [x] 2.1 `history-partitions` health check, degraded below 2 future months; `/healthz` says `degraded`; verify `Health_is_degraded_when_future_partitions_run_low` and `Health_is_healthy_with_partitions_ahead`
- [x] 2.2 `baton.limits.corrections` counter tagged by limit kind, registered with OpenTelemetry; verify `Limit_correction_is_counted_by_kind`

## 3. Close

- [x] 3.1 `dotnet build` clean, `dotnet test` green for Domain, Agent, Web and Integration, `openspec validate --all --strict` green
