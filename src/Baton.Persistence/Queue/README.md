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
| Due cron triggers | §7 "Due cron triggers" | M4 — `ON CONFLICT (idempotency_key) DO NOTHING` |

Lock order wherever an attempt ends: run row → task row → downstream rows → `group-tasks` row
(M3 design.md "Implementation notes").

Rules: all times from Postgres `now()`; every state change in one transaction; lock
`concurrency_limits` rows in key order (ADR-0009). Every query here needs an integration test
in `tests/Baton.Integration.Tests` that exercises concurrency (two claimers, stale token, expiry).
