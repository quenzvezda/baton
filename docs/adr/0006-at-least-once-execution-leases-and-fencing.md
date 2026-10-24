# ADR-0006: At-least-once execution with leases and fencing tokens

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §3 (Execution guarantee), §6 "Leases and fencing", ORC-07, ORC-08, ORC-09

## Context

Agents crash, pause for GC, or lose network. Exactly-once execution of arbitrary side effects is not
achievable; what we can guarantee is that no task runs twice **at the same time** and that a stale
worker cannot overwrite a newer result.

## Decision

- Delivery is **at-least-once**. Handlers must be idempotent and receive `IdempotencyKey = runId:taskKey`,
  stable across retries.
- A claim sets `lease_token = nextval('lease_token_seq')` (strictly increasing fencing token) and
  `lease_expires_at = now() + lease_seconds` (default 60 s, per group).
- One heartbeat every 10 s renews every lease the host still owns (matched by id **and** token).
- `Start`, `Complete` and `AppendLogs` carry the token; a mismatch returns `FAILED_PRECONDITION` and
  the agent discards the result.
- The reaper (5 s) ends expired attempts as `Lost`, then retries per policy or fails.
- All times come from Postgres `now()`, never agent clocks.

## Consequences

**Positive**
- A dead host's work recovers in ~1 minute; 60 s vs 10 s tolerates five missed heartbeats.
- Split-brain results are rejected deterministically.

**Negative / risks**
- Side effects can repeat → handler idempotency is a documented contract and a review checklist item.
- Long-running tasks rely on heartbeats staying healthy → per-group lease tuning; checkpointing is an
  open question (OQ-02).

## Alternatives considered

| Option | Why not |
| --- | --- |
| Visibility timeout without fencing | Late results from a presumed-dead worker would overwrite newer attempts. |
| Exactly-once via distributed transactions | Not possible for arbitrary external side effects. |
