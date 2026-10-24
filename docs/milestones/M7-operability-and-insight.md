# M7 — Operability & insight

**Phase:** 2 · **Change:** `add-operability-and-insight` *(create when starting)* · **Depends on:** G1

## Goal

Operators see problems early, are notified, can recover failed runs cheaply and can audit changes.

## Scope

| ID | Requirement |
| --- | --- |
| AUT-11 | Audit log of every automation (and host-group) change: who, when, before, after |
| WF-10 | Re-run a failed run from its failed tasks, reusing successful outputs |
| OBS-04 | Queue dashboard: queued tasks and oldest wait per group, active leases, lease expirations/hour |
| OBS-05 | Failure/timeout notifications by email or webhook, per automation |
| OBS-06 | OpenTelemetry export of traces, metrics and logs |

## ADRs / open questions

OQ-08 (should a failed notification task fail the run?).

## Exit criteria

- [ ] Change created, validated, implemented and archived
- [ ] Re-run reuses outputs and records the new attempts on the same run (or a linked run — decide in design)
- [ ] OQ-08 answered
