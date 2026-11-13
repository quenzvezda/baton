# Runbook: stuck queue

**Symptom:** runs stay *Pending*, or their tasks stay *Queued*, although hosts look idle.

A run moves Pending → Running at admission (every limit it needs has room, ORC-02); its tasks move
Queued → Leased when an eligible host claims them (ORC-04). Find which of the two is stuck first.

## 1. Where is it stuck?

```sql
SELECT g.name AS "group", r.status, count(*), min(r.created_at) AS oldest
FROM workflow_runs r JOIN host_groups g ON g.id = r.host_group_id
WHERE r.status IN ('pending', 'running')
GROUP BY 1, 2 ORDER BY 1, 2;

SELECT g.name AS "group", count(*) AS queued, now() - min(t.queued_at) AS oldest_wait
FROM task_runs t JOIN host_groups g ON g.id = t.host_group_id
WHERE t.status = 'queued'
GROUP BY 1 ORDER BY oldest_wait DESC;
```

Old **pending** runs → section 2. Old **queued** tasks → section 3.

## 2. Pending runs: admission

```sql
SELECT r.id, r.blocked_on, r.created_at, l.max, l.in_use
FROM workflow_runs r LEFT JOIN concurrency_limits l ON l.key = r.blocked_on
WHERE r.status = 'pending' ORDER BY r.created_at LIMIT 20;
```

- `blocked_on` names the limit the run last waited for (`automation:…`, `workflow:…`, `group-runs:…`).
- **`in_use` = `max` and that many runs are really active** → working as designed: the limit is full. Raise
  the limit (automation editor, `PUT /api/workflows/{key}/limit`, host group edit) or wait.
- **`in_use` is higher than the active runs holding it** → a drifted counter. The limit reconciler corrects
  it within 5 minutes and logs `Limit … corrected`; `baton.limits.corrections` increases. Wait one interval;
  file a bug with the log line, since every correction means a missed release.

```sql
-- Live holders of one limit, to compare with in_use.
SELECT count(*) FROM workflow_runs
WHERE status IN ('running', 'cancelling') AND '<limit key>' = ANY (held_limits);
```

- **`blocked_on` is empty and nothing is admitted** → admission is not running. Check that at least one
  orchestrator is up (`/healthz`) and look for `AdmissionLoop tick failed` errors.

## 3. Queued tasks: claims

A host claims a task only if **all** hold: the host is Online (heartbeat within 30 s, not draining), it
carries the task's definition hash, it has every required label, it has a free slot, and the group is under
`max_concurrent_tasks`.

```sql
-- Hosts of the group and what they can take.
SELECT h.name, h.status, h.drain_requested, now() - h.last_heartbeat_at AS since_heartbeat,
       h.capacity_slots, h.labels, h.agent_version,
       (SELECT count(*) FROM task_runs t WHERE t.lease_host_id = h.id AND t.status IN ('leased','running')) AS busy
FROM hosts h JOIN host_groups g ON g.id = h.host_group_id
WHERE g.name = '<group>' ORDER BY h.name;

-- What the queued tasks need.
SELECT t.definition_hash, t.required_labels, count(*)
FROM task_runs t JOIN host_groups g ON g.id = t.host_group_id
WHERE g.name = '<group>' AND t.status = 'queued' GROUP BY 1, 2;
```

| Finding | Cause | Fix |
| --- | --- | --- |
| No host Online | Agents down, cannot reach the orchestrator, or all draining | Start agents; check their logs for `Heartbeat … failed`; undrain (`POST /api/hosts/{id}/undrain`) |
| No host's `definition_hashes` contains the task's hash | The workflow version was registered by a newer build than the agents run | Roll out agents built with that version ([agent rollout](agent-rollout.md)) |
| Labels never covered | Task or automation requires a label no host has | Fix the automation's labels or add the label to an agent (`Agent:Labels`) |
| Every host's `busy` = `capacity_slots` | Saturated, not stuck | Add agents or slots |
| `group-tasks:<id>` `in_use` = `max` | Group task limit full | Raise `max_concurrent_tasks` or wait; drift is fixed by the reconciler as in section 2 |

Claims are woken by `NOTIFY` and re-check every 2 s anyway, so a lost `LISTEN` connection only adds up to
2 s, never a stall.

## Confirm recovery

The oldest queued wait (first query of section 1) drops below a few seconds and runs leave Pending.
