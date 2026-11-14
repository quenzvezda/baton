# M7 — queue statistics query over G1-sized data

**Date:** 2026-11-14 · **Branch:** `m7/operability-and-insight` · **Test:**
`M7QueueStatsLoadTests.Queue_stats_stay_fast_over_g1_sized_data`
(`dotnet test tests/Baton.Integration.Tests -c Release --filter FullyQualifiedName~M7QueueStatsLoadTests`)

**Result: pass.** The OBS-04 statistics query (`QueueStats.Sql`, design.md decision 24) serves every figure
from a partial index and runs in about 2 ms on data the size of the G1 profile. That is cheap enough for the
API, for the console's 5 s refresh and for the orchestrator's 15 s gauge refresh on every replica.

| Check | Measured | |
| --- | --- | --- |
| `QueueStats.ReadAsync`, 20 calls after one warm-up | p50 **3.3 ms**, p95 **3.9 ms**, max 4.3 ms (round trip from the test process) | ✅ (< 100 ms asserted) |
| Server execution (`EXPLAIN ANALYZE`) | 1.8 ms execution, 0.6 ms planning, 1,745 shared buffers hit, no reads | ✅ |
| Every aggregate uses its partial index | `ix_tasks_dispatch` (queued), `ix_runs_admission` (pending), `ix_tasks_leases` (active), `ix_attempts_lost` on each partition (lost in the last hour) | ✅ |
| Figures correct | busy group: 5,000 queued, 24 active leases, 120 lease expirations in the last hour | ✅ |

## Data

Seeded with SQL in one Testcontainers Postgres 16 (default settings), then `ANALYZE`:

- **4 host groups**, the shape of the G1 rig with room for more than one group.
- **4,500 finished runs × 4 tasks** (18,000 task runs and attempts) over the last day, spread over the groups.
  This is one G1 load run, the daily volume of the 10k-task-runs/day profile.
- **300,000 older attempts** (retries) of those tasks, finished hours to days ago this month. One in 50 of them
  is `lost`, so the partial index on lost attempts also holds old rows that the one-hour window must skip.
- **The busy group:** the M6 backlog of 5,000 queued tasks, 24 running tasks (6 agents × 4 slots) and 120
  attempts lost within the last hour.

## Plan

```text
Nested Loop (actual time=0.237..1.710 rows=4 loops=1)
  -> Index Scan using host_groups_name_key on host_groups g (rows=4)
  -> Aggregate (loops=4)
       -> Bitmap Heap Scan on task_runs t (rows=1250 loops=4)
            -> Bitmap Index Scan on ix_tasks_dispatch   Index Cond: (host_group_id = g.id)
  -> Aggregate (loops=4)
       -> Index Only Scan using ix_runs_admission on workflow_runs r   Index Cond: (host_group_id = g.id)
  -> Aggregate (loops=4)
       -> Index Scan using ix_tasks_leases on task_runs t_1 (rows=6 loops=4)
  -> Aggregate (loops=4)
       -> Nested Loop (rows=30 loops=4)
            -> Append (rows=120 loops=4)
                 -> Bitmap Heap Scan on task_attempts_y2026m11
                      -> Bitmap Index Scan on task_attempts_y2026m11_finished_at_idx
                           Index Cond: (finished_at >= (now() - '01:00:00'::interval))
                 -> Seq Scan on task_attempts_y2026m12 / y2027m01 / y2027m02 (empty future partitions, rows=0)
            -> Index Scan using task_runs_pkey on task_runs t_2 (loops=480)
Planning Time: 0.626 ms
Execution Time: 1.849 ms
```

## Notes

- **The lease-expiration aggregate grows with groups × lost attempts per hour.** Each group reads the hour's
  lost attempts and keeps its own. Here that is 4 × 120 index lookups. A lease-expiry storm of thousands of
  lost attempts per hour, across many groups, would make this the slow part. The runbook already treats such
  a storm as an incident ([lease-expiry spike](../runbooks/lease-expiry-spike.md)). If it ever matters, the
  next step is to group the lost attempts once instead of per group.
- **Future partitions show as sequential scans with no rows.** They are empty, and Postgres scans them in
  microseconds. Once a month fills, `ix_attempts_lost` exists on it as on every partition (task 1.1,
  `Lost_attempt_index_exists_on_every_partition`).
- **The query takes no row locks.** It never waits on claims, the finisher or the reaper.
