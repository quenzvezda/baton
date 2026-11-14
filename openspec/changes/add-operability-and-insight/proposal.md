# Proposal

## Why

**Milestone M7 — Operability & insight** (phase 2, after Gate G1). Requirements: AUT-11, WF-10, OBS-04,
OBS-05, OBS-06; open question OQ-08. v1 runs work reliably, but operators find out about problems late
and recover from them by hand:

- Nothing records who changed an automation or a host group, or what the change was.
- A run that fails in its last task must start again from the first.
- There is no view of queue depth or lease expirations.
- A failed or timed-out run notifies nobody unless its workflow has its own notify task.
- Only the API and orchestrator emit telemetry. The agents, which run the handlers, emit none.

Gate G2 needs alerts on queue age and lease expirations, and nothing measures either yet.

## What Changes

- **Audit log (AUT-11).** Every change to these is recorded with who, when, what kind of change, and the
  state before and after:
  - automations, including their triggers, notifications, enable/disable and secret rotation;
  - host groups, including their limits and pause/resume;
  - agent credentials;
  - workflow run limits.

  Secrets never appear in an entry. Entries are kept for 1 year and can be read by entity, newest first.
- **Re-run failed tasks (WF-10).** An Operator re-runs a Failed or TimedOut run in place. The run keeps its
  id, parameters and definition version, and goes back through admission.
  - Tasks that succeeded keep their outputs and do not run again.
  - Every other task that the failure affected runs again, with attempt numbers that continue and a fresh
    retry budget.
  - The run records how many times it was re-run. Its event timeline shows each re-run.
- **Queue statistics (OBS-04).** For each host group the API and the console show:
  - queued tasks and the oldest wait;
  - active leases;
  - lease expirations in the last hour;
  - pending runs.

  The same figures are exported as metrics, so G2 can alert on them.
- **Run notifications (OBS-05).** Each automation has a list of notification channels, email or webhook,
  each set to fire on Failed, TimedOut or both. Delivery is at least once, with retries.
  - A webhook is signed the same way Baton verifies inbound webhooks.
  - A delivery that still fails after its retries is recorded on the run and counted in a metric. It never
    changes the run's outcome. This answers **OQ-08**: a failed notification is reported, it does not fail
    the run.
- **OpenTelemetry everywhere (OBS-06).**
  - The agent and the console export traces, metrics and logs, as the API and orchestrator already do.
  - Every handler attempt is a span in the run's trace.
  - Handler log lines also go to the log exporter.
  - New metrics cover queue depth and age, active leases, lease expirations and notification outcomes.
- Console:
  - a Queue page;
  - a Re-run failed tasks action on the run detail page;
  - a Notifications section in the automation editor;
  - a History view of audit entries for automations and host groups.

Out of scope:
- Alert rules and dashboards in a monitoring tool (Gate G2).
- Notifications on Succeeded or Cancelled runs, and on individual task failures.
- Notification channels shared between automations.
- Editing parameters or switching definition versions on re-run (a re-run repeats the same run).
- An audit trail of run actions such as cancel and re-run. These are already in the run event timeline.
- Resource pools and workflows edited in the UI (M8).

## Capabilities

### New Capabilities

- `audit-trail`: recording and reading who changed configuration, when, and from what to what (AUT-11).
- `notifications`: per-automation notification channels and their at-least-once delivery on run failure
  or timeout (OBS-05, OQ-08).

### Modified Capabilities

- `run-lifecycle`: re-running a Failed or TimedOut run from its failed tasks (WF-10).
- `run-monitoring`: queue statistics per host group (OBS-04). The run detail shows the re-run count (WF-10).
- `operations`: telemetry export from every process, handler spans in the run's trace, and the queue,
  lease and notification metrics (OBS-04, OBS-05, OBS-06).
- `access-control`: the role list names re-run, notifications and the audit log.
- `web-console`: the queue page, the re-run action, notification settings in the editor, and the audit
  history view.

## Impact

- `Baton.Domain`: re-run planning over the graph (which tasks reset, their counters, their initial state),
  audit redaction rules, notification retry schedule.
- `Baton.Persistence`: one migration, `OperabilityAndInsight`. It adds the audit table, the notification
  channel and delivery tables, a re-run counter and a trace context on runs, and an index for lost
  attempts. It also adds re-arming in admission, the notification enqueue inside run finalization, the
  queue statistics query, and audit retention in daily maintenance.
- `Baton.Orchestrator`: a notification delivery loop, a queue metrics loop, lease-expiration counting in
  the reaper, and access to the shared Data Protection key ring.
- `Baton.ControlPlane.Api`: `POST /api/runs/{id}/retry-failed`, `GET /api/queue/stats`, `GET /api/audit`,
  and notification channels on automations. Every configuration endpoint writes an audit entry.
- `Baton.Agent`: telemetry wiring and a handler span.
- `Baton.Web`: telemetry wiring and the console pages above.
- `agent.proto`: additive field `TaskLease.trace_parent = 10`. Not breaking.
- REST contract: additive fields and endpoints only.
- New packages: `MailKit`. Local development adds Mailpit to `docker-compose.yml`.
- ADRs: new ADR-0018 (notification outbox) and ADR-0019 (email delivery). The design is constrained by
  ADR-0003, ADR-0006, ADR-0007, ADR-0008, ADR-0009, ADR-0011, ADR-0014 and ADR-0015.
