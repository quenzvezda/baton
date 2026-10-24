# Queue SQL (hot path)

Hand-written Npgsql/Dapper SQL for the paths that decide throughput and correctness (ADR-0007):

| Operation | Spec | Notes |
| --- | --- | --- |
| Claim | §7 "Claim" | `FOR UPDATE SKIP LOCKED`, fresh `lease_token` per claim |
| Heartbeat | §7 "Heartbeat" | renews only rows whose token still matches |
| Complete | §7 "Complete" | fenced by token, DAG advance in the same transaction |
| Due cron triggers | §7 "Due cron triggers" | `ON CONFLICT (idempotency_key) DO NOTHING` |
| Expired leases | §7 "Expired leases" | reaper batch of 500 |

Rules: all times from Postgres `now()`; every state change in one transaction; lock
`concurrency_limits` rows in key order (ADR-0009). Every query here needs an integration test
in `tests/Baton.Integration.Tests` that exercises concurrency (two claimers, stale token, expiry).
