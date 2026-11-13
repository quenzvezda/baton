# Gate G1 — load test report

**Date:** 2026-11-14 · **Branch:** `g1/v1-production-readiness` · **Test:**
`G1LoadTests.Twenty_dispatches_per_second_for_fifteen_minutes_meets_the_latency_targets`
(`dotnet test tests/Baton.Integration.Tests --filter Category=G1`)

**Result: pass.** Every Performance target of G1 (revised by OQ-01 on 2026-11-14) is met with a wide margin.

| G1 target | Measured | |
| --- | --- | --- |
| 10k task runs/day profile, peaks of 20 dispatches/s sustained for 15 min | 20.0 dispatches/s average over 901 busy seconds (peak 24/s); 18,060 tasks in 15 min — the daily volume of the profile in under a quarter hour | ✅ |
| p95 Queued→Leased < 2 s with free slots | p95 **14 ms** (p50 7, p99 20, max 63 ms) | ✅ |
| Cron runs created within 5 s | 15 of 15 cron runs; avg 55 ms, max **61 ms** late | ✅ |
| No accepted run lost | 4,500 accepted, 4,500 Succeeded (+15 cron) | ✅ |

## Setup

- **Machine:** one developer workstation — Intel Core i5-12400F (6 cores / 12 threads), 32 GB RAM, Windows 11,
  Docker Desktop. Everything ran on it at once: Postgres, both orchestrators, six agents and the producer.
- **Postgres:** `postgres:16-alpine` in a Testcontainers container, default settings except
  `max_connections=400`; schema from the EF migrations.
- **Orchestrators:** 2 replicas in-process (Kestrel, HTTP/2), all loops on both, production dispatch defaults
  (claim re-check 2 s, admission 1 s, reaper 5 s, trigger 1 s).
- **Agents:** 6 real agents (`AddBatonAgent`, gRPC, claim long-poll) × 4 slots = 24 slots, three preferring
  each replica. Lease 60 s (default).
- **Workload:** workflow `g1-pipeline` — `fetch` → (`left`, `right`) → `merge`, each task sleeping
  50–400 ms — fired through the run-creation path at 5 runs/s (= 20 dispatches/s) for 15 minutes, plus a
  cron automation firing every minute. Every run goes through creation, admission, claims, DAG advance and
  finalization.

## Results

| Metric | Value |
| --- | --- |
| Runs by status | succeeded 4515 |
| Runs accepted by the producer / not Succeeded | 4500 / 0 |
| Fires refused (never accepted) | 0 |
| Task attempts (dispatches) | 18060 |
| Dispatch rate, busy seconds avg / peak | 20.0 /s / 24 /s over 901 s |
| Queued→Leased p50 / p95 / p99 / max | 7 / 14 / 20 / 63 ms |
| Cron runs; lateness avg / max | 15; 55 / 61 ms |
| Lost attempts (reaped leases) | 0 |
| Overlapping leases of one task (DB) | 0 |
| Tasks with more than one accepted success | 0 |
| Handler executions / overlapping executions of one task | 18060 / 0 |

| At | Event |
| --- | --- |
| 00:00 | producer starts: 5 runs/s × 4 tasks; cron every minute |
| 15:00 | producer stops |
| 15:01 | every run final |

## How it is measured

- **Queued→Leased:** per attempt, `task_attempts.started_at` (lease time) minus the task's `queued_at`, both
  Postgres `now()`. Slots were free throughout (≈5 of 24 busy on average: 5 runs/s × 4 tasks × ~225 ms), so this is pure dispatch latency:
  `NOTIFY` wakes a waiting claim within milliseconds; the 2 s re-check never mattered.
- **Dispatch rate:** attempts per second, over the seconds in which at least one lease was granted.
- **Cron lateness:** `workflow_runs.created_at` minus the fire time carried in the run's idempotency key.
- **Fencing checks:** no attempt was leased before the previous attempt of the same task was closed; no task
  has two accepted successes; the handler-side tracker saw no task executing twice at the same time.

## Limits of this test

- One machine, so the numbers say little about network latency; they do show the design has headroom: the
  M3 throughput smoke sustained 50 claims/s on one group.
- Handlers only sleep, so the database load is Baton's own, not the workload's.
- 15 minutes, not days: vacuum and partition behavior over weeks are covered by the retention tests, not here.
- `BATON_G1_LOAD_MINUTES` shortens the run for a rehearsal (a 1-minute run gave the same latencies).
