# Runbook: lease-expiry spike

**Symptom:** many attempts end with outcome `lost`; tasks are retried although their handlers did not fail.

An attempt is `lost` when its lease expired before the agent completed it: the agent stopped heartbeating
(crashed, partitioned, overloaded) or its heartbeats were refused. The reaper then retries the task
(at-least-once, ADR-0006). Any result the old holder sends later is rejected by its fencing token, so no
result is applied twice — but the handler may have run twice, which is why handlers must be idempotent.

## 1. How big, where, since when

```sql
SELECT date_trunc('hour', a.finished_at) AS hour, g.name AS "group", count(*) AS lost
FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id JOIN host_groups g ON g.id = t.host_group_id
WHERE a.outcome = 'lost' AND a.finished_at > now() - interval '24 hours'
GROUP BY 1, 2 ORDER BY 1 DESC, 3 DESC;

-- Which hosts held the lost leases.
SELECT h.name, h.status, now() - h.last_heartbeat_at AS since_heartbeat, count(*) AS lost
FROM task_attempts a JOIN hosts h ON h.id = a.host_id
WHERE a.outcome = 'lost' AND a.finished_at > now() - interval '1 hour'
GROUP BY 1, 2, 3 ORDER BY lost DESC;
```

## 2. Read the pattern

| Pattern | Likely cause | Action |
| --- | --- | --- |
| One or few hosts, now Offline | Agent process or machine died | Expected: tasks were retried elsewhere. Replace the host; remove it once Offline (`DELETE /api/hosts/{id}`) |
| One host, still Online | Heartbeats reach the orchestrator, but the agent lost track of the task (`Running task … crashed` in its log) | Collect the agent log, restart the agent |
| All hosts of a group at once | Network between agents and orchestrators, or the orchestrators were down longer than the lease | Check orchestrator `/healthz` and agent logs (`Heartbeat … failed`); see [Postgres outage](postgres-outage.md) |
| Long tasks only, hosts healthy | Heartbeat interval vs lease too tight under load (heartbeat is `min(10 s, lease / 6)`) | Raise the group's `lease_seconds` (30–3600) |
| Spike right after a deploy | Agents stopped without draining | Follow [agent rollout](agent-rollout.md) |

Lease expiry uses Postgres time only; agent or orchestrator clock skew cannot cause it.

## 3. When retries run out

A task whose attempts are exhausted fails its run. Find them:

```sql
SELECT r.id AS run_id, t.task_key, t.attempt, t.max_attempts, t.error
FROM task_runs t JOIN workflow_runs r ON r.id = t.workflow_run_id
WHERE t.status = 'failed' AND t.finished_at > now() - interval '1 hour'
  AND EXISTS (SELECT 1 FROM task_attempts a WHERE a.task_run_id = t.id AND a.outcome = 'lost');
```

Re-run them with Run now on their automation once the cause is fixed; there is no in-place retry in v1.

## Confirm recovery

The hourly `lost` count returns to its baseline (zero in the G1 load test) and the affected hosts are
Online again or removed.
