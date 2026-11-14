# Proposal

## Why

**Milestone M6 — Scheduling & routing controls** (phase 2, after Gate G1). Requirements: AUT-08, AUT-10,
ORC-06, ORC-12, ORC-13. v1 decides *whether* work runs; operators now need finer control over *when* and
*where*. After an outage every cron trigger fires its missed time once, whatever the job. Low-priority
tasks can wait forever behind a steady stream of high-priority ones. Workflows that share local files
cannot keep all their tasks on one host. The only way to stop a group taking work is to drain every host
in it. The schema has carried the columns for these features since M0 (`misfire_policy`, `placement_mode`,
`dispatch_paused`, `effective_priority`, `pinned_host_id`), but nothing reads or writes them yet.

## What Changes

- **Misfire policy per cron trigger (AUT-08).** A fire time more than 60 s late is *missed*. FireOnce
  (default) creates one run for the most recent missed time. Skip creates none. CatchUp creates runs for
  the most recent N missed times (N per trigger, 1–10, default 3). Fire times that are on time always fire.
  Every fire time that is passed over is recorded.
- **Fire-time preview (AUT-10).** The API returns the next 5 fire times of a cron expression in its time
  zone. The automation editor shows them live while the expression is edited.
- **Priority aging (ORC-06).** A queued task gains +1 effective priority per 5 minutes of waiting, up to
  +20 and at most 100. The step and cap are global orchestrator settings. A task that is queued again
  (after a retry) starts from its base priority.
- **Placement mode per automation (ORC-12).** PerTask (default, today's behavior) or PinnedRun. Under
  PinnedRun the first claim pins the run to that host, which must carry every label the run's tasks need.
  Later tasks go only to that host. If the pinned host goes Offline, is removed, or is moved to another
  group, the run fails with "pinned host lost" (OQ-04).
- **Pause and resume dispatch per host group (ORC-13).** Operators pause a group. Its hosts receive no new
  tasks; leased and running tasks finish. Admission continues. Resuming wakes waiting agents at once.
- Console: misfire and placement fields and the fire-time preview in the automation editor; host groups
  with a paused badge and Pause/Resume actions on the hosts page.

Out of scope: an audit trail of who paused a group or changed a policy (AUT-11 → M7); queue dashboard and
wait-time metrics per group (OBS-04 → M7); aging Pending runs at admission (ORC-06 covers waiting tasks
only); re-pinning a run to another host (OQ-04 answered "fail"); resource pools (M8).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `automations`: misfire policy and catch-up limit on cron triggers (AUT-08), the fire-time preview
  (AUT-10), placement mode on the automation (ORC-12).
- `task-dispatch`: host eligibility gains the paused-group and pinned-run conditions; adds priority
  aging (ORC-06) and PinnedRun pinning and its "pinned host lost" failure (ORC-12).
- `host-registry`: pause and resume dispatch per group (ORC-13). A draining host still takes the
  remaining tasks of runs pinned to it (HST-04 with ORC-12).
- `web-console`: the editor's misfire, placement and preview fields; group pause on the hosts page.

## Impact

`Baton.Domain` (misfire selection, aging formula), `Baton.Persistence` (one migration: trigger catch-up
limit, run placement and required labels, the new cancel reason; claim, trigger, retry and reaper SQL),
`Baton.Orchestrator` (`AgingLoop`, settings, wake-up on resume), `Baton.ControlPlane.Api` (automation and
trigger fields, the preview endpoint, group pause and resume), `Baton.Web` (editor, hosts page).
REST contract: additive fields and endpoints only. No `agent.proto` change, no new package.
ADR-0003, ADR-0006, ADR-0007, ADR-0008, ADR-0009.
