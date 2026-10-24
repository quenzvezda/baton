# Proposal

## Why

Milestone **M4 — Automations, triggers & run lifecycle**. With definitions (M1), hosts (M2) and dispatch
(M3) in place, users need to configure automations and have triggers create runs that are admitted
under limits, finish with a correct status, time out and can be cancelled. This completes the v1
backend. Requirements: AUT-01..AUT-07, AUT-09, ORC-01, ORC-02, ORC-11, plus NFR-TriggerAccuracy.

## What Changes

- Automations API (create, read, update with `If-Match`, delete, clone, enable/disable) with parameter validation against the definition's schema.
- Triggers: cron with IANA time zone (Cronos), manual Run now, HMAC-signed webhooks.
- Trigger loop (1 s) with idempotent run creation and concurrency policies Skip / Queue / Replace / Allow.
- Admission loop (1 s, advisory lock per group) reserving automation, workflow and group-run limits.
- Run finalization (status from task outcomes, limit release), run timeout, cancellation flow, limit reconciler.

Out of scope: misfire policies AUT-08, cron preview AUT-10, automation audit log AUT-11 (M6/M7);
Run now overrides AUT-12 (M8); notifications OBS-05 (M7); editor **page** (M5).

## Capabilities

### New Capabilities

- `automations`: user-configured bindings of triggers, workflow, parameters, routing, priority and concurrency behavior.
- `run-lifecycle`: how a workflow run is created, admitted, finished, timed out and cancelled.

### Modified Capabilities

None.

## Impact

`Baton.ControlPlane.Api` (automations, Run now, webhooks), `Baton.Orchestrator` (TriggerLoop,
AdmissionLoop, LimitReconcilerLoop, finalization hook from M3), `Baton.Persistence`. ADR-0008, ADR-0009.
Depends on OQ-07 (secret store) for the parameter guidance.
