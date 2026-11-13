# Proposal

## Why

**Gate G1 — v1 production readiness**, Operations criterion: "Partition creation and 90-day retention
running; limit reconciler alerting on corrections". Not a roadmap milestone; it closes gaps left by M0
and M3. M0 creates monthly history partitions ahead of time (ADR-0011) but nothing removes old history,
so the database grows without bound. M3's limit reconciler only logs its corrections, so no alert rule
can watch them. Requirements: NFR-Retention, NFR-Auditability (corrections are observable).

## What Changes

- The daily partition maintenance also enforces the 90-day retention: history partitions whose whole
  month is older than 90 days are dropped, and finished runs older than 90 days are purged with their tasks.
- Orchestrator health reports *degraded* when fewer than 2 future monthly partitions exist for any
  history table, so a stalled maintenance loop is visible before inserts start failing.
- Every limit correction increments a counter metric (exported through OpenTelemetry when an endpoint is
  configured) besides the existing warning log.

Out of scope: queue dashboard and full OpenTelemetry export setup (OBS-04, OBS-06 → M7); notifications
(OBS-05 → M7); archiving history to object storage before the drop (ADR-0011 alternative, not needed for v1).

## Capabilities

### New Capabilities

- `operations`: housekeeping that keeps a deployment healthy over time — history retention, partition
  health and limit-correction signals.

### Modified Capabilities

None.

## Impact

`Baton.Persistence` (retention SQL), `Baton.Orchestrator` (partition loop, health check, metrics),
`src/Shared/BatonTelemetry.cs` (registers Baton meters). No schema migration, no new package. ADR-0011.
