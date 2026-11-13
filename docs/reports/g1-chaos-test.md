# Gate G1 — chaos test report

**Date:** 2026-11-14 · **Branch:** `g1/v1-production-readiness` · **Tests:** `G1ChaosTests`
(`dotnet test tests/Baton.Integration.Tests --filter Category=G1`), on the same workstation and rig as the
[load test](g1-load-test.md).

**Result: pass.** Under continuous load, killing an agent mid-task, stopping one of two orchestrator replicas
and restarting Postgres lost no accepted run, never granted two leases of one task at once, never accepted two
results of one task, and never had a task executing twice at the same time. A rolling replacement of every
agent under load failed no run and lost no attempt. One defect found on the way is fixed (finding F1).

| G1 criterion | Result | |
| --- | --- | --- |
| Kill an agent, kill one of two orchestrator replicas, restart Postgres under load: no accepted run lost, no task running twice concurrently | 344 accepted, 344 Succeeded; 0 overlapping leases; 0 double results; 0 overlapping executions | ✅ |
| Rolling agent upgrade under load fails no runs (drain + definition-hash matching) | 212 accepted, 212 Succeeded; 0 lost attempts | ✅ |

## Failures under load

**Setup:** 2 orchestrator replicas (all loops on both; reaper 1 s, retry promoter 0.5 s, claim re-check 0.5 s),
8 agents × 3 slots, half preferring each replica and failing over to the other on a connection failure (as a
load balancer would), lease 30 s — the shortest a group allows, still longer than the Postgres outage.
Workload: 2 runs/s of `g1-pipeline` (4 tasks of 0.3–3 s each, ≈13 of 24 slots busy) for 3 minutes.

| At | Event |
| --- | --- |
| 00:00 | producer starts: 2 runs/s × 4 tasks of 0.3–3 s |
| 00:30 | agent `agent-4a153c8a51b3` killed (network cut) holding 3 running tasks — the busiest agent |
| 01:15 | orchestrator replica 2 of 2 stopped; its agents fail over to replica 1 |
| 02:00 | Postgres stopped |
| 02:10 | Postgres started again (10 s outage) |
| 03:00 | producer stops |
| 03:06 | every run final |

| Metric | Value |
| --- | --- |
| Runs by status | succeeded 344 |
| Runs accepted by the producer / not Succeeded | 344 / 0 |
| Fires refused (never accepted) | 16 — all during the Postgres outage; the caller got an error, so nothing was accepted |
| Task attempts (dispatches) | 1379 |
| Dispatch rate, busy seconds avg / peak | 7.8 /s / 19 /s over 177 s |
| Queued→Leased p50 / p95 / p99 / max | 7 / 16 / 108 / 286 ms |
| Lost attempts (reaped leases) | 3 — exactly the killed agent's 3 tasks, retried on other agents and succeeded |
| Overlapping leases of one task (DB) | 0 |
| Tasks with more than one accepted success | 0 |
| Handler executions / overlapping executions of one task | 1379 / 0 |

What each failure did:

- **Agent killed.** Its 3 leases expired after 30 s; the reaper marked the attempts `lost` and retried the
  tasks elsewhere. Its later reports were refused (fencing). Its host went Offline.
- **Orchestrator replica stopped.** Its agents' next calls failed to connect and moved to replica 1; claims
  resumed on replica 1 (no Queued→Leased wait above 0.3 s in the whole run). Singleton loops (reaper, reconciler, partitions) and per-group admission simply ran
  on the survivor — the advisory locks needed no hand-over.
- **Postgres restarted.** For 10 s every loop tick failed and was retried (`… tick failed`), every agent call
  answered `UNAVAILABLE` and was retried by the agents, and `LISTEN` reconnected with backoff. No lease expired
  (30 s > 10 s), so no task was retried and no work was redone; work queued during the outage was dispatched
  as soon as Postgres was back.

## Rolling agent upgrade

**Setup:** as above with 6 agents × 3 slots; 2 runs/s for 1:46. At 00:20 six new agents start; then every
10 s one old agent is stopped normally (drain: it stops claiming, finishes its running tasks, reports them,
exits).

| Metric | Value |
| --- | --- |
| Runs accepted / not Succeeded | 212 / 0 |
| Task attempts | 848 |
| Queued→Leased p50 / p95 / max | 7 / 13 / 29 ms |
| Lost attempts | 0 — every task running on a stopping agent finished there |
| Overlapping leases / double results / overlapping executions | 0 / 0 / 0 |

Old agents held 1–3 running tasks each when stopped; all completed before the agent exited. Definition-hash
matching is not exercised here — all agents run the same build, so they carry the same hashes; that a host
claims only tasks whose hash it carries is covered by the M3 claim tests.

## Findings

**F1 (fixed) — agents dropped completions during a Postgres outage.** In the first runs, while Postgres was
down the orchestrator answered agent calls with `UNKNOWN` (an unhandled `NpgsqlException`), which agents
correctly do not retry. Completions sent during the outage were dropped, their leases expired after the restart
and the tasks ran again: 14 extra `lost` attempts in one run. Work was redone, never lost, but needlessly.
Fix: `DatabaseUnavailableInterceptor` maps connection-level database failures to `UNAVAILABLE`, which agents
retry (about 8 s with backoff). Test: `DatabaseUnavailableTests`. After the fix the same scenario lost no
attempt to the outage (3 lost = the killed agent's).

**F2 (test rig, fixed) — failover ignored by the gRPC client.** Given a `SocketsHttpHandler`, `Grpc.Net.Client`
opens its own connections straight to the channel's address, so the test's failover handler had no effect and
agents of the stopped replica stayed idle: capacity fell to two agents and queue waits reached a minute. The rig
now gives the failover handler an `HttpClientHandler`. Production agents use one orchestrator address behind a
load balancer and are not affected.

**Observations, no action:**

- An outage longer than the group's lease would expire running leases at the restart and re-run those tasks
  while their holders may still run them (fenced, so results are applied once). Keep `lease_seconds` above the
  longest expected Postgres outage — the [Postgres outage runbook](../runbooks/postgres-outage.md) says so.
- During the outage, agents log `Host … reported leases it no longer owns` a few times after retried calls;
  outcomes are unaffected (no lost attempt beyond the killed agent's).
- "Kill" is a network cut for the agent and a host stop for the orchestrator replica, both in-process: a killed
  agent's handlers keep running locally, which makes the overlap check stricter, not weaker.
