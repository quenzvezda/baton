# Design

## Context

See proposal.md. The SQL shapes are in design spec §7 "Hot-path queries" (Claim, Heartbeat, Complete,
Expired leases). Constraints: ADR-0003 (one transaction per transition), ADR-0006 (fencing),
ADR-0007 (hand-written SQL), ADR-0008 (SKIP LOCKED loops), ADR-0009 (group-tasks counter).

## Goals / Non-Goals

**Goals:** p95 < 2 s Queued→Leased with a free slot; no task leased to two hosts at once; a dead host's
work recovered in about one lease period; 50 claims/s sustained on one group in the integration rig.

**Non-Goals:** run admission and triggers (M4); aging (M6); preemption (out of scope for v1).

## Decisions

- **Claim transaction.** (1) lock `group-tasks:{id}` row if the group has a task limit and compute the
  budget = min(free slots, room); (2) `SELECT … FOR UPDATE SKIP LOCKED` ordered by
  `effective_priority DESC, queued_at` with eligibility predicates; (3) update to Leased with
  `nextval('lease_token_seq')`; (4) increment `in_use`; insert `task_attempts` rows; commit.
  Host status/draining/heartbeat-age and group are read from the host row in the same transaction.
- **Long-poll.** The gRPC handler waits on an in-process signal fed by a single `LISTEN tasks_ready`
  connection per replica (payload = group id), re-checking every 2 s regardless; returns early on the
  first non-empty claim or at `max_wait`.
- **Complete.** Fenced `UPDATE … WHERE lease_token = @token` returning 0 rows ⇒ `FAILED_PRECONDITION`.
  Downstream keys come from the cached definition graph (by hash). Rows reaching `upstream_remaining = 0`
  are evaluated with the domain trigger-rule function; skips propagate iteratively in the same
  transaction. Then release `group-tasks`, `NOTIFY tasks_ready, '<group>'`. Run finalization is a hook
  completed in M4.
- **Start.** Leased → Running fenced by token; sets `started_at`. Optional for correctness, used for timing.
- **Retry classification.** Failed + retryable + attempts left ⇒ Retry wait with `not_before = now() + backoff`;
  timeout counts as retryable; non-retryable or exhausted ⇒ Failed.
- **Reaper (5 s).** Expired leases ⇒ attempt `lost`; `cancel_requested` ⇒ Cancelled (no retry); otherwise
  retry classification. Also fails tasks running past `timeout + 60 s` as a backstop.
- **Agent.** Claims only free slots; per-task `CancelAfter(timeout)`; heartbeat reply's
  `lost_task_ids` cancels the handler and suppresses reporting; `cancel_task_ids` cancels and reports Cancelled.
  Logs are buffered and streamed with `AppendLogs`, fenced by token.

## Decisions agreed with the owner (2026-10-26)

1. **OQ-03: PinnedRun stays in M6.** No workflow needs all its tasks on one host yet. The claim query
   keeps the `pinned_host_id` predicate (it costs nothing), but nothing sets it before M6. OQ-04
   (re-pin or fail) stays open for M6.
2. **OQ-02: lease 60 s, heartbeat 10 s by default, tunable per group** (`host_groups.lease_seconds`,
   30–3600). Checkpointing multi-hour tasks is out of scope. The heartbeat interval a host is told at
   registration is `min(10 s, lease / 6)`, so a shorter lease still tolerates five missed beats.
3. **Free slots are checked by the server.** The claim budget is
   `min(free_slots from the agent, capacity_slots − leases the host holds, room in group-tasks)`.
   The host's held leases are counted from `task_runs` (`ix_tasks_by_host`) in the claim transaction.
4. **Claims check the host row.** In the claim transaction the host must be Online by the M2 rule
   (`HostSql.IsEligible`: heartbeat ≤ 30 s, not drained, not shutting down); the group, labels and
   definition hashes come from the same row. `dispatch_paused` (ORC-13) is left to M6.
5. **The graph comes from the database.** Complete, the reaper and the claim read the task graph
   (downstream keys, ancestors, retry policy, timeout) from `workflow_definitions.graph` by
   `definition_hash`, parsed once and cached per hash in the process. An orchestrator build never
   needs to contain the definitions it dispatches, so rolling deploys are safe.
6. **Upstream outputs travel inline in `TaskLease`**: the outputs of every succeeded ancestor of the
   task (`TaskContext.UpstreamOutputs` is ancestors, not parents). The gRPC message limit is raised to
   64 MB on both sides. A separate fenced `GetUpstreamOutputs` RPC is the fallback if that is ever too
   small; it would be an additive `v1` change.
7. **Minimal run finalization in M3.** In the transaction that makes the last task of a Running run
   final (Complete or reaper), the run becomes Failed if any task Failed, otherwise Succeeded, with
   `finished_at = now()` and a `run.succeeded`/`run.failed` event. Releasing `held_limits`, Cancelling →
   Cancelled and the run deadline (TimedOut) stay in M4.
8. **Limit reconciler in M3.** `LimitReconcilerLoop` (singleton, every 5 min) locks every
   `concurrency_limits` row in key order and recomputes `in_use`: `group-tasks:{id}` from leased and
   running tasks of the group, every other key from active runs (`running`, `cancelling`) whose
   `held_limits` contain it. Rows it corrects are logged as warnings.
9. **Tests.** The chaos test runs real `AgentWorker`s in-process against an in-process orchestrator;
   "killing" an agent cuts its gRPC channel, so it neither heartbeats nor reports, exactly like a dead
   process seen from the server. The 50 claims/s × 60 s throughput test carries
   `[Trait("Category", "Load")]`, is excluded from CI, and its report is attached to the PR.

Defaults taken without objection: the timeout backstop is measured from the attempt's Start, or from
its claim when Start never arrived; run-event types are `task.queued`, `task.skipped`, `task.leased`,
`task.started`, `task.succeeded`, `task.failed`, `task.timed_out`, `task.lost`, `task.cancelled`,
`task.retry_scheduled`, `run.started`, `run.succeeded`, `run.failed` (a due retry is `task.queued` with
`"retry": true`); the branch is `m3/task-dispatch`.

## Implementation notes (M3)

- **Lock order** everywhere a task finishes (Complete, reaper): the `workflow_runs` row, then the task
  row, then downstream rows of the run, then the `group-tasks` row. Completes in one run are serialized
  by the run row, so the "is every task final?" check can never miss a concurrent completion. Claims
  lock the `group-tasks` row and then take task rows with `SKIP LOCKED`, so they never wait on a task
  row. The reaper picks candidates without locks and closes each one in its own transaction with the
  same lock order, re-checking expiry, so two replicas never close an attempt twice.
- **An expired lease is lost even before the reaper runs.** Heartbeat renewal, Start, Complete and
  AppendLogs all require `lease_token = @token AND lease_expires_at > now()`; a heartbeat for an
  expired lease returns the task in `lost_task_ids`.
- **Schema addition** (migration `TaskDispatch`, `Schema/0003_task_dispatch.sql`): `task_runs.leased_at`,
  the claim time of the current attempt. It is the `started_at` key of the attempt's row in
  `task_attempts` (so the attempt is updated by its full primary key) and the fallback for the timeout
  backstop.
- **Releasing `group-tasks`** uses `GREATEST(in_use - n, 0)`: a limit row created after the claim must
  not violate `in_use >= 0`; the reconciler corrects the count.
- **Outcomes reported by the agent.** Succeeded with output over 256 KB ⇒ Failed, not retryable.
  Failed ⇒ retry classification with the reported `retryable`. TimedOut ⇒ retryable. Cancelled ⇒
  Cancelled (final, no retry) when cancellation was requested; otherwise the host stopped the handler on
  its own and it is a retryable failure.
- **Reaper** (every 5 s): expired leases (lost), attempts running past `timeout + 60 s` (timed out,
  retryable) and, per M2 decision 9, persisting `hosts.status = 'offline'` for hosts past the threshold.
  `RetryPromoterLoop` (every 1 s) moves due Retry wait tasks to Queued with `SKIP LOCKED`, then
  notifies their groups.
- **Wake-up.** `TasksReadyListener` holds the one `LISTEN tasks_ready` session (the non-pooled `listen`
  data source) and reconnects with backoff; waiters in `Claim` re-check every 2 s regardless.
  Every transaction that queues a task or frees a group slot ends with `pg_notify('tasks_ready', group)`.
- **Log times.** `task_logs.logged_at` keeps the agent's time for display, clamped to
  `[now() − 1 day, now() + 5 min]` so a wrong clock cannot miss a partition; order comes from `seq`, and a
  retried chunk (same `seq` and time) is deduplicated by the primary key.
- **Handlers that ignore cancellation** keep their slot on the agent until they return; the platform has
  already closed the attempt (timeout backstop), the next heartbeat reports it lost and its result is
  discarded.
- **Agent side.** `AgentWorker` runs a claim loop next to the register/heartbeat loop; a lease won while
  a drain arrived is still run (`AgentLifecycle.TaskLeased`). `TaskExecutor` resolves the handler by graph
  hash and task key; `TaskLogBuffer`/`TaskLogShipper` ship logs every second and before the report.
  Agent unit tests live in the new `tests/Baton.Agent.Tests` (fake `AgentService` client).
- **Run start.** `RunMaterializer` (Persistence) creates the `task_runs` of a Running run: roots
  Queued, the rest Waiting, base priority `clamp(run priority + offset, 0, 100)`, labels = automation's ∪
  task's. Tests seed runs through it now; M4 admission calls it.

## Risks / Trade-offs

- [Hot `group-tasks` row serializes claims] → only when a task limit is set; measure; batch claims.
- [NOTIFY storms] → one notify per completing transaction per group; claims coalesce.
- [Long GC pauses exceed lease] → 60 s lease vs 10 s heartbeat; per-group tuning (OQ-02).
- [Skip propagation over large DAGs in one transaction] → bounded by DAG size; test with 500-task DAG.

## Open Questions

- Default long-poll and lease values per group are tunable; final defaults confirmed at Gate G1 load test.
