# M7 — Operability & insight

**Phase:** 2 · **Change:** `add-operability-and-insight` · **Depends on:** G1

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

ADR-0018 (notification outbox), ADR-0019 (email with MailKit), OQ-08 (should a failed notification task fail the run?).

## Exit criteria

- [ ] Change created, validated, implemented and archived
- [ ] Re-run reuses outputs and records the new attempts on the same run (decided: same run, change design.md decision 1)
- [x] OQ-08 answered: a failed notification is reported, it never fails the run (change design.md decision 6, 2026-11-14)
