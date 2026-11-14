# Runbook: Postgres outage and restart

**Symptom:** API calls fail with 5xx, orchestrator `/healthz` reports `unhealthy` (`postgres` check),
agents log `Heartbeat … failed`, webhooks fail.

Postgres is Baton's only state (ADR-0003). While it is down nothing changes state: no run is created,
admitted, claimed or finished, so nothing is lost either — every transition is one transaction that either
committed before the outage or never happened. G1 runs without a streaming replica (OQ-01, 2026-11-14), so
recovery means bringing the same server back.

## During the outage

1. **Confirm it is Postgres**, not the network to it: `pg_isready -h <host> -p 5432` from an orchestrator
   host; orchestrator `/healthz` → `checks.postgres` = `Unhealthy`.
2. **Bring it back**: restart the server or container (`docker compose restart postgres` locally). Check the
   Postgres log for the reason (out of disk, OOM kill, crash) before restarting a second time.
3. **Do not restart the orchestrators or agents.** They reconnect by themselves: the connection pools drop
   broken connections, the `LISTEN` session reconnects with backoff (claims poll every 2 s meanwhile),
   failed loop ticks retry on the next tick, agents retry heartbeats and claims. Expected log noise:
   `… tick failed`, `… failed: database unavailable …; answered UNAVAILABLE`, `LISTEN connection failed`.
   The G1 chaos test restarted Postgres under load with no run lost and no task re-run
   ([report](../reports/g1-chaos-test.md)).

## What happens to work in flight

- **Running tasks keep running** on their agents. Leases are measured in Postgres time; if the outage is
  shorter than the group's `lease_seconds` (default 60 s), the next heartbeat after the restart renews them
  and nothing is retried.
- **An outage longer than the lease** makes those leases expire the moment Postgres is back: the reaper
  retries the tasks while their old holders may still be running them. The old holders learn of the loss on
  their next heartbeat and stop; their late results are rejected by the fencing token. Expect a burst of
  `lost` attempts ([lease-expiry spike](lease-expiry-spike.md)) and handlers running twice — idempotent
  handlers make that harmless.
- **Agent calls during the outage** are answered `UNAVAILABLE` and retried by the agents for about 8 s
  (G1 chaos finding F1). A completion still failing after that is dropped; its lease then expires and the
  task is retried — the work is redone, never lost.
- **Cron fires due during the outage** are created once on recovery (misfire policy `fire_once`), not once
  per missed minute.
- **Webhook senders** get errors and must retry with the same `X-Delivery-Id`; duplicates are recognized.

## After recovery

```sql
-- Nothing should stay non-final for long after the restart.
SELECT status, count(*), min(created_at) FROM workflow_runs
WHERE status IN ('pending', 'running', 'cancelling') GROUP BY 1;

-- Lost attempts caused by the outage.
SELECT count(*) FROM task_attempts WHERE outcome = 'lost' AND finished_at > now() - interval '30 minutes';
```

Orchestrator `/healthz` returns `ok`; the stuck-queue queries show normal waits; the next limit
reconciler pass logs no corrections (any correction is a bug — report it).

## If data is damaged

Restore from the last backup ([backup and restore](backup-restore.md)). Runs accepted after that backup
are gone; webhook senders and cron re-create what is still due, manual runs must be started again.
