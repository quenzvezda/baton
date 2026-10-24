# Design

## Context

See proposal.md. Constraints: ADR-0005 (pull-based gRPC), ADR-0009 (limit rows), ADR-0013 (agent auth,
Proposed — accept before this change closes). Contract: `src/Baton.Agent.Protocol/Protos/agent.proto`.

## Goals / Non-Goals

**Goals:** a host appears, stays Online while heartbeating, goes Offline 30 s after the last heartbeat
without any orchestrator-local state; any orchestrator replica can serve any agent.

**Non-Goals:** lease renewal semantics (M3 completes Heartbeat), autoscaling.

## Decisions

- **Identity.** Host name is unique; re-registration with the same name updates the existing row
  (labels, slots, version, hashes) and keeps the host id. Registration requires a valid agent token
  scoped to the target group (ADR-0013).
- **Offline detection.** Status is derived lazily: a host is Offline when `now() - last_heartbeat_at > 30 s`.
  A periodic sweep (part of the reaper in M3) persists `status='offline'` for the hosts page and events;
  dispatch eligibility always uses the timestamp, never only the stored status.
- **Drain.** `draining` is set by operators via API; the agent learns it from the Heartbeat reply and
  stops claiming. Undrain returns to Online on the next heartbeat.
- **Group move.** Changing `host_group_id` takes effect on the next claim because claims read the group
  from the host row inside the claim transaction.
- **Limits.** Creating/updating a group upserts `group-runs:{id}` / `group-tasks:{id}` rows; `NULL` limit
  deletes the row (unlimited). Lowering a limit below `in_use` is allowed; new work waits until it drains.
- **Agent resilience.** `Microsoft.Extensions.Http.Resilience` for retries with jitter on transient gRPC errors.

## Risks / Trade-offs

- [Clock-free liveness relies on DB time] → heartbeat writes `now()` server-side; agent clocks ignored.
- [Deleting a group in use] → rejected while hosts or automations reference it.
