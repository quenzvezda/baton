# Design

## Context

See proposal.md. Constraints: ADR-0003 (Postgres only), ADR-0006 (leases and fencing), ADR-0007
(hand-written SQL on the claim, trigger and reaper paths), ADR-0008 (`SKIP LOCKED` row loops,
transaction-scoped advisory locks for singletons), ADR-0009 (limit rows locked in key order), "Time comes
from Postgres `now()`". Design spec §6 "Routing and eligibility" and "Priority and aging", §7 "Claim" and
"Due cron triggers".

Today's state, from the code:

- The Baseline migration already has `automation_triggers.misfire_policy` (`fire_once|skip|catch_up`,
  default `fire_once`), `automations.placement_mode` (`per_task|pinned_run`), `host_groups.dispatch_paused`,
  `workflow_runs.pinned_host_id`, `task_runs.pinned_host_id` and `task_runs.effective_priority` (with the
  partial index `ix_tasks_dispatch (host_group_id, effective_priority DESC, queued_at) WHERE status = 'queued'`).
  None of them is exposed by the API or read by the orchestrator except
  `task_runs.pinned_host_id IS NULL OR = @hostId` in the claim, which is always NULL today.
- `CronTriggers` fires the *oldest* missed time once (it passes `next_fire_at` as the fire time), then
  moves `next_fire_at` past `now()`. Enabling a trigger or an automation recomputes `next_fire_at` from
  `now()`, so misfires happen only after platform downtime, not after a disable.
- `RetryPromoter` and `TaskFinisher` set `queued_at = now()` when they queue a task but leave
  `effective_priority` alone.
- `RunCancellation` has three reasons (`user`, `replaced`, `timeout`; `workflow_runs.cancel_reason` CHECK)
  and `RunFinalizer` maps `timeout` to TimedOut.
- `SingletonLoop` (advisory-locked tick) and the `tasks_ready` NOTIFY channel (payload: host group id)
  already exist.

## Goals / Non-Goals

**Goals:** no change of behavior for automations that keep the defaults (FireOnce, PerTask, no pause),
except that FireOnce now uses the most recent missed time (decision 2); claims stay one transaction that
never waits on a lock it can skip; aging writes only rows whose value changes.

**Non-Goals:** aging Pending runs at admission; a per-group aging setting (decision 9); re-pinning
(decision 1); an audit trail of pause and policy changes (AUT-11, M7).

## Decisions agreed with the owner (2026-11-14)

1. **OQ-04: a run whose pinned host is lost fails.** No re-pin, no restart from the start. The run fails
   with reason "pinned host lost" (spec "Pinned host lost").
2. **Misfire: a fire time more than 60 s late is missed; CatchUp runs the N most recent missed times,**
   in time order. N is per trigger, 1–10, default 3. FireOnce is CatchUp with N = 1 and Skip is N = 0, so
   FireOnce now fires the most recent missed time instead of the oldest (an intended change: the run gets
   the fire time closest to the data it should process).
3. **Aging step and cap are global orchestrator settings** (`Dispatch:AgingStep`, default `00:05:00`;
   `Dispatch:AgingCap`, default 20; next to the other dispatch timings), not per group. No schema, API or UI for them.
4. **Pause stops claims only.** Admission continues, so runs of a paused group become Running and their
   tasks queue. Run deadlines keep counting while a group is paused. Operators may pause and resume (the
   access-control spec already lists "pause" under Operator).

**Proposed here, to confirm in review:**

5. **A draining host keeps serving runs already pinned to it** (spec "Draining hosts", modified). Without
   this a rolling upgrade would leave every pinned run on that host waiting until its deadline. The drain
   still stops new runs from pinning to the host, so it empties once its pinned runs finish. The agent
   therefore keeps claiming while an operator drains it (`AgentLifecycle.CanClaim` is true when Draining,
   not when shutting down). The orchestrator decides what a draining host gets. An agent older than M6
   stops claiming on drain, so its pinned runs wait until their deadline: upgrade agents before using
   PinnedRun.

## Decisions

6. **Misfire planning is pure domain code.** `Baton.Domain.Automations.MisfirePlanner.Plan(schedule,
   nextFireAt, now, policy, catchUpLimit)` walks the fire times in `[next_fire_at, now()]`. Times with
   `now() − t ≤ 60 s` are on time. The rest are missed, and only the last N are kept, in a queue, so
   memory stays bounded however long the outage. The walk is exact (30 days of a per-minute trigger, 43,200
   times, takes about 10 ms), so there is no cap. It returns the fire times to create (selected missed times, then the
   on-time ones), the missed count with the first and last missed time, and the next fire time after
   `now()`. Unit-tested with a fixed `now`. `CronTriggers.FireNextAsync` calls it and fires each selected
   time through `RunCreator.FireAsync` in the trigger's existing transaction. The idempotency key format is
   unchanged (one key per fire time), so a replayed pass never creates a run twice. The concurrency policy
   applies to each firing on its own. Passed-over times are recorded as one `trigger.misfired` run event
   under the automation id (like `run.skipped`), with `{ triggerId, count, first, last, policy }`. That
   bounds the event count by passes, not by missed times.
7. **Trigger fields.** Migration: `automation_triggers.catch_up_limit smallint NULL CHECK (catch_up_limit
   BETWEEN 1 AND 10)`, plus a CHECK that it is set exactly when `misfire_policy = 'catch_up'`. API:
   `TriggerRequest`/`TriggerResponse` gain `misfirePolicy` (`fire_once|skip|catch_up`, same casing as
   `concurrencyPolicy`) and `catchUpLimit`. A CatchUp trigger saved without a limit gets 3. A limit on
   another policy is rejected with a field error. Webhook triggers reject a misfire policy other than the
   default.
8. **Fire-time preview.** `GET /api/cron/preview?expression=&timeZone=` (Viewer) returns
   `[{ at: <UTC instant>, local: <local date-time>, offset }]` for up to 5 times after the database's
   `now()`, using `CronSchedule` (Cronos). Invalid input → 400 problem details keyed `cronExpression` /
   `timeZone`, the same messages as saving. The editor calls it debounced (400 ms) per cron trigger.
9. **Aging loop.** `AgingLoop : SingletonLoop`, 30 s, new `AdvisoryLocks.Aging`. One statement:
   ```sql
   WITH aged AS (
     SELECT id, LEAST(100, priority + LEAST(@cap, floor(extract(epoch FROM now() - queued_at) / @stepSeconds)))::smallint AS p
     FROM task_runs
     WHERE status = 'queued'
       AND effective_priority <> LEAST(100, priority + LEAST(@cap, floor(extract(epoch FROM now() - queued_at) / @stepSeconds)))
     FOR UPDATE SKIP LOCKED)
   UPDATE task_runs t SET effective_priority = aged.p FROM aged WHERE t.id = aged.id
   ```
   The `<>` filter writes a task at most once per step (at most `cap` times in its life). Each write adds an
   entry to `ix_tasks_dispatch`, which autovacuum cleans up. `SKIP LOCKED` means the tick never waits on a
   claim. A row a claim holds is about to leave Queued anyway, and any other skipped row is aged on the
   next tick. No NOTIFY: aging never makes a task newly eligible. Settings are validated at startup (step ≥ 30 s, cap 0–100; cap 0
   disables aging), and the formula is in `Baton.Domain.Runs.TaskPriority` for unit tests.
10. **Re-queued tasks start from base.** `RetryPromoter` and `TaskFinisher` set `effective_priority =
    priority` wherever they set `queued_at = now()`. `priority` on `task_runs` is already the base
    (run priority + offset, clamped).
11. **Pin state lives on the run.** Migration: `workflow_runs.placement_mode text NOT NULL DEFAULT 'per_task'`
    (copied from the automation by `RunCreator`, so an edit never changes an active run),
    `workflow_runs.pin_labels text[] NOT NULL DEFAULT '{}'` (union of every task's required labels,
    written by `RunMaterializer` at admission) and `task_runs.pinned_run boolean NOT NULL DEFAULT false`.
    The unused `task_runs.pinned_host_id` is dropped: one source of truth for the pin, and no fan-out
    update to every task of the run when it is pinned, which would lock rows the finisher may hold.
    Partial index `ix_runs_pinned ON workflow_runs (pinned_host_id) WHERE pinned_host_id IS NOT NULL`.
12. **Pinning in the claim transaction.** The candidate CTE gains:
    `AND (NOT t.pinned_run OR EXISTS (SELECT 1 FROM workflow_runs r WHERE r.id = t.workflow_run_id AND
    (r.pinned_host_id = @hostId OR (r.pinned_host_id IS NULL AND @labels @> r.pin_labels))))`. PerTask
    tasks skip the subquery. For the picked tasks of unpinned runs, the claim then runs
    `UPDATE workflow_runs SET pinned_host_id = @hostId WHERE id IN (SELECT id FROM workflow_runs WHERE id =
    ANY(@runs) AND pinned_host_id IS NULL FOR UPDATE SKIP LOCKED) RETURNING id`. Only tasks of PerTask runs,
    of runs already pinned to this host, and of runs this statement pinned are leased. The rest are
    released when the transaction ends and stay Queued. `SKIP LOCKED` means a claim never waits on another
    claim or the finisher (no new lock-order edge; ADR-0009 order is unchanged). The cost is a smaller
    batch in a rare race. A `run.pinned` event records the host. Draining (decision 5): the host-row check
    (`HostSql.IsEligible`) today zeroes the budget for a draining host. It becomes "Online, or draining
    with at least one Running run pinned to it". The candidate CTE for a draining host additionally
    requires `t.pinned_run` and a run pinned to it.
13. **Pinned host lost** is detected by the `ReaperLoop` pass (5 s, `SKIP LOCKED`, row-oriented like the
    deadline check): Running runs with `pinned_host_id` whose host has no heartbeat for 30 s (the
    `HostLiveness` rule), no longer exists, or belongs to a group other than the run's. Each goes through
    `RunCancellation` with the new reason `pinned_host_lost` (CHECK extended). `RunFinalizer` maps it to
    Failed and writes `run.failed` with `{ reason: "pinned host lost", hostId }`. Leased tasks on the dead
    host close as Cancelled at lease expiry (existing ORC-11 path). The host-removal check ("A run is
    pinned to this host") stays: an Offline host's runs have already failed, so removal is only blocked for
    the ≤ 5 s window before the reaper sees it.
14. **Pause.** `POST /api/host-groups/{id}/pause` and `/resume` (Operator), idempotent, return the group.
    The claim's host-row SQL reads `g.dispatch_paused` and zeroes the budget when it is set, so a paused
    group leases nothing, pinned runs included. Resume commits `dispatch_paused = false` and
    `pg_notify('tasks_ready', group id)` in one transaction, which wakes long-polling agents through the
    existing listener (NFR-DispatchLatency). Pause events are logged with `[LoggerMessage]`. The audit row
    comes with AUT-11 in M7.
15. **Console.** Automation editor: per cron trigger a misfire select with a catch-up number shown for
    CatchUp, and the preview list (local time and UTC). Automation-level placement select. Hosts page: a
    group header row per group with a paused badge and Pause/Resume for Operator and Admin (role-aware
    like Drain). bUnit tests against the fake API.

## Risks / Trade-offs

- [Aging churns the dispatch index under a large backlog] → at most one write per queued task per step.
  Measured under the G1 load profile (M6 exit criterion; task 3.4). If it hurts, a partial index on
  `queued_at` for the aging scan is the next step.
- [A pinned host re-registers without a label or definition hash the run needs] → its tasks wait. The run
  deadline (AUT-09) bounds the wait. Documented in the runbook "agent rollout": drain hosts with pinned
  runs before changing their labels or builds.
- [CatchUp under the Skip concurrency policy creates fewer runs than N] → intended. Each firing obeys the
  automation's policy. Scenario "Catch-up runs follow the concurrency policy" makes it visible.
- [FireOnce now uses the most recent missed time] → noted under Migration. Idempotency keys of runs
  already created are unaffected.
- [A long outage with a per-minute cron] → the walk is linear in missed times but holds only N. A
  year of a per-minute trigger is about 525,000 steps, roughly 0.1 s once for that trigger.

## Migration Plan

One EF migration `SchedulingAndRouting` (hand-written SQL where CHECKs and indexes are involved, like
earlier migrations): `catch_up_limit` with its CHECKs, `workflow_runs.placement_mode` and `pin_labels`,
`task_runs.pinned_run`, drop `task_runs.pinned_host_id`, `ix_runs_pinned`, the extended `cancel_reason`
CHECK. All columns have defaults, so existing rows need no backfill and existing runs stay PerTask.
Rollback: the Down migration restores the dropped column (always NULL before M6). The REST changes are
additive. Release notes mention the FireOnce fire-time change.
