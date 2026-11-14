# M6 — priority aging under the G1 load profile

**Date:** 2026-11-14 · **Branch:** `m6/scheduling-and-routing-controls` at `71feec2` · **Test:**
`M6AgingLoadTests.Aging_a_backlog_under_the_g1_load_writes_only_changed_rows_and_keeps_dispatch_fast`
(`dotnet test tests/Baton.Integration.Tests -c Release --filter FullyQualifiedName~M6AgingLoadTests`)

**Result: pass.** M6 exit criterion "Aging keeps the dispatch index usable (only changed rows updated) —
measured under G1 load profile":

| Check | Measured | |
| --- | --- | --- |
| Only changed rows are written | 100,000 writes = 5,000 backlog tasks × cap 20, exactly; none of the 18,060 G1 tasks was ever written | ✅ |
| Dispatch stays fast while aging writes | p95 Queued→Leased **14 ms** (G1 baseline 14 ms); p99 22 ms (20); max 156 ms (63) | ✅ |
| G1 load still met | 20.0 dispatches/s over 901 s, 4,500 of 4,500 accepted runs Succeeded, no lost attempt, no overlap | ✅ |
| Backlog aged in step | every backlog task at effective priority 30 (base 10 + cap 20) at the end | ✅ |

## Setup

The G1 load rig unchanged ([G1 load report](g1-load-test.md) "Setup": one workstation, Postgres 16 in
Testcontainers, 2 orchestrator replicas with every loop, 6 agents × 4 slots, `g1-pipeline` fired at 5 runs/s
for 15 minutes plus a per-minute cron), running the M6 code: the PinnedRun-aware claim, the misfire planner
in the trigger loop and the aging loop. Two changes make aging do real work:

- **A backlog that waits.** Before the producer starts, 5,000 queued tasks (base priority 10) are inserted
  into the same group, each requiring a label no agent has. They stay Queued for the whole run and sit in
  `ix_tasks_dispatch` next to the live tasks the claims scan.
- **A 30 s aging step** (`Dispatch:AgingStep`, production default 5 min) with the default cap of 20 and the
  default 30 s loop interval, so the backlog climbs to its cap within 10 minutes: a worst case of 5,000
  writes per tick, every tick, for the first 10 minutes. With the 5 min default the same backlog would take
  100 minutes to reach the cap and write a tenth as often.

## Results

| Metric | Value |
| --- | --- |
| Runs by status | succeeded 4515 |
| Runs accepted by the producer / not Succeeded | 4500 / 0 |
| Task attempts (dispatches) | 18060 |
| Dispatch rate, busy seconds avg / peak | 20.0 /s / 27 /s over 901 s |
| Queued→Leased p50 / p95 / p99 / max | 7 / 14 / 22 / 156 ms |
| Cron runs; lateness avg / max | 15; 284 / 292 ms |
| Lost attempts / overlapping leases / double successes | 0 / 0 / 0 |
| Aging ticks that wrote rows | 20 (one per step until the cap) |
| Rows written in all / per writing tick | 100,000 / 5,000 each |
| `ix_tasks_dispatch` size before / after | 48 / 336 KiB |

## Reading the numbers

- **Only changed rows.** The loop writes a row only when its computed priority differs from the stored one
  (`effective_priority <> …`). Each backlog task was written once per step, 20 times, and then never again;
  the G1 tasks wait milliseconds, so their priority never changes and they were never written. Counted with
  the `baton.tasks.aged` counter on both replicas.
- **The index stays usable.** Every write is a non-HOT update that adds an index entry; the index grew from
  48 to 336 KiB over 100,000 writes and autovacuum (`autovacuum_vacuum_scale_factor = 0.01` on `task_runs`)
  keeps the dead entries reusable. Claims kept their latency: p50 and p95 equal the G1 baseline. The
  higher max (156 ms) is one outlier among 18,060 attempts.
- **Cron lateness** is 284–292 ms for all 15 fires, against 55–61 ms in the G1 run. The value is constant
  within a run: it is the phase of the 1 s trigger loop against the minute boundary, set when the
  orchestrators start. It says nothing about aging, and it stays far inside the 5 s target.

## Limits of this test

- The same as the G1 load test: one machine, sleeping handlers, 15 minutes.
- The backlog ages in lockstep (all inserted at once), so every writing tick touches all 5,000 rows; real
  backlogs are spread out and write fewer rows per tick.
- `BATON_G1_LOAD_MINUTES` shortens the run; below about 11 minutes the backlog does not reach its cap and the
  exact write count is not checked.
