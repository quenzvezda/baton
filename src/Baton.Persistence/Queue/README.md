# Queue SQL (hot path)

Hand-written Npgsql/Dapper SQL for the paths that decide throughput and correctness (ADR-0007):

| Operation | Spec | Notes |
| --- | --- | --- |
| Register, heartbeat | M2 | `AgentRegistry` |
| Claim | §7 "Claim" | `TaskClaims`: host must be Online; budget = free slots ∩ host capacity ∩ `group-tasks`; `FOR UPDATE SKIP LOCKED`, fresh `lease_token` per claim |
| Lease renewal | §7 "Heartbeat" | `TaskLeases.RenewAsync`: renews only current, unexpired leases |
| Start, AppendLogs | ORC-08 | `TaskLeases`: fenced by token, expiry and credential |
| Complete | §7 "Complete" | `TaskLeases.CompleteAsync` + `TaskFinisher`: fenced, DAG advance, run outcome, limit release, NOTIFY in one transaction |
| Expired leases, timeout backstop | §7 "Expired leases" | `LeaseReaper`: batch of 500, one transaction per attempt |
| Due retries | ORC-10 | `RetryPromoter`: `SKIP LOCKED`, NOTIFY per group |
| Limit counters | §7 "Operational notes" | `LimitReconciler`: rows locked in key order |
| Run start | ORC-03 | `RunMaterializer`: roots Queued, the rest Waiting |
| Run creation | ORC-01, AUT-07 | `RunCreator`: automation row locked; idempotency key looked up first; policy decides; Replace cancels in the same transaction |
| Due cron triggers | §7 "Due cron triggers" | `CronTriggers`: one trigger per transaction, trigger and automation rows `SKIP LOCKED`, `next_fire_at` from Cronos |
| Admission | ORC-02 | `RunAdmission`: group advisory lock on a side connection; one transaction per run, limit rows in key order, pass-over |
| Run outcome, limit release | "Run outcome" | `RunFinalizer` + `LimitRows`: held limits and `group-tasks` released together, in key order |
| Cancellation | ORC-11, AUT-09 | `RunCancellation`: run row → task rows → limit rows; the reaper closes cancelled attempts after 30 s and cancels runs past `deadline_at` |

Lock order wherever an attempt ends: run row → task row → downstream rows → limit rows in key order
(`group-tasks` and, when the run ends, the limits it held). Run creation: automation row → the replaced
run → its tasks → limit rows. Admission locks one run (`SKIP LOCKED`) and then its limit rows in key order,
one run per transaction, so it never waits on a run row.

Rules: all times from Postgres `now()`; every state change in one transaction; lock
`concurrency_limits` rows in key order (ADR-0009). Every query here needs an integration test
in `tests/Baton.Integration.Tests` that exercises concurrency (two claimers, stale token, expiry).
