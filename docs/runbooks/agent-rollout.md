# Runbook: agent rollout

**Goal:** upgrade, replace or restart agents without failing or slowing runs (NFR Rolling deploys).

Two mechanisms make this safe: **drain** (a draining host finishes its running tasks and claims nothing
new, HST-04) and **definition-hash matching** (a host only claims tasks whose definition hash its build
carries). An agent drains itself on a normal stop (SIGTERM / service stop): it reports draining, stops
claiming, keeps heartbeating its running tasks until they finish, then exits.

## Before

1. If the new build carries **new workflow versions**, deploy the Control Plane API first: it registers
   the definitions at startup, so their hashes exist before any agent can run them. Old agents never claim
   tasks of a version they do not carry, so old and new agents can run side by side. The new build must
   still carry the old versions: runs started before the switch keep their version, and once the last old
   agent is gone only the new build can finish them (G1 chaos report, "Rolling agent upgrade").
2. Know the longest task duration in the group. A stopping agent must be given that long (the host's or
   orchestrator's stop timeout) or its running tasks are cut and retried.
3. Confirm the group has room: during the rollout capacity shrinks by the agents being replaced.

## Rolling the agents

Repeat per agent (or per batch of at most a quarter of the group):

1. Start the new agent (new build, same group token). It registers and starts claiming.
2. Stop the old agent normally — **not** `kill -9`. Or drain it from the console first
   (`POST /api/hosts/{id}/drain`) and stop it once it shows no running tasks.
3. Wait until the old host shows no running tasks and goes Offline (30 s without heartbeat), then remove it
   (`DELETE /api/hosts/{id}`, Offline hosts only).

```sql
-- Old builds still holding work.
SELECT h.name, h.agent_version, h.status, h.drain_requested,
       (SELECT count(*) FROM task_runs t WHERE t.lease_host_id = h.id AND t.status IN ('leased','running')) AS running
FROM hosts h JOIN host_groups g ON g.id = h.host_group_id
WHERE g.name = '<group>' ORDER BY h.agent_version, h.name;
```

**PinnedRun automations (M6, ORC-12).** Every task of a pinned run runs on the host that took its first task.
A host drained from the console keeps taking the remaining tasks of runs pinned to it, and nothing else, so
wait until it shows no running tasks **and** no running run is pinned to it before stopping it:

```sql
SELECT r.id, r.status FROM workflow_runs r JOIN hosts h ON h.id = r.pinned_host_id
WHERE h.name = '<host>' AND r.status IN ('running','cancelling');
```

Stopping the agent (SIGTERM) instead of draining it ends its claims at once. If it stays away for 30 s, its
pinned runs fail with "pinned host lost". Never change a pinned host's labels or remove a definition version
from its build while runs are pinned to it: the remaining tasks would wait until the run's timeout. Agents
built before M6 stop claiming when drained, so upgrade every agent of a group before using PinnedRun in it.

Kubernetes: a Deployment rolling update does steps 1–2 for you; set `terminationGracePeriodSeconds` above
the longest task duration and `maxUnavailable` to keep capacity.

## If something goes wrong

- **Queued tasks pile up for a new workflow version** → no Online agent carries its hash yet; see
  [stuck queue](stuck-queue.md) section 3.
- **`lost` attempts during the rollout** → agents were killed instead of stopped, or the stop timeout was
  shorter than the running tasks; tasks were retried. See [lease-expiry spike](lease-expiry-spike.md).
- **Runs fail with "pinned host lost"** → their pinned host was stopped or killed and stayed away 30 s,
  or was moved to another group. Drain hosts with pinned runs first (above).
- **Rollback** → the same procedure with the old build; runs of a version only the new build carries wait
  Queued until a carrier is back.

## Confirm

All hosts of the group report the new `agent_version`, none are draining, and no run failed during the window:

```sql
SELECT status, count(*) FROM workflow_runs
WHERE host_group_id = (SELECT id FROM host_groups WHERE name = '<group>')
  AND finished_at > now() - interval '1 hour' GROUP BY 1;
```
