# Proposal

## Why

Milestone **M3 — Task dispatch & leases**. This is the core of the engine: deciding which tasks are
ready, which host gets each one, and keeping that assignment honest when hosts crash or stall.
Requirements: ORC-03, ORC-04, ORC-05, ORC-07, ORC-08, ORC-09, ORC-10, plus NFR-DispatchLatency and
NFR-ExecutionGuarantee.

## What Changes

- DAG advance on completion: decrement upstream counters, apply trigger rules, queue or skip, propagate skips — in the completing transaction.
- Claim (gRPC long-poll up to 20 s) with eligibility filtering, group task budget, priority ordering and fenced leases; `LISTEN/NOTIFY` wake-up with 2 s re-check.
- `Start`, `Heartbeat` lease renewal, `Complete` with fencing, `AppendLogs` persistence.
- Reaper (expired leases → Lost → retry or Failed) and retry promoter (Retry wait → Queued).
- Agent `AgentWorker` + `TaskExecutor`: per-task timeout, cancellation from heartbeat replies, result reporting.
- Attempt history in `task_attempts`; run events for every task transition.

Out of scope: creating runs from triggers and admission (M4 — this milestone seeds running runs
directly in tests), priority aging ORC-06 (M6), PinnedRun ORC-12 (M6), pause dispatch ORC-13 (M6).

## Capabilities

### New Capabilities

- `task-dispatch`: task readiness, host eligibility and ordering, leases and fencing, lost-lease recovery, retries and timeouts.

### Modified Capabilities

None.

## Impact

`Baton.Orchestrator` (AgentService, ReaperLoop, RetryPromoterLoop), `Baton.Persistence/Queue`,
`Baton.Agent`, `Baton.Domain` (trigger rules, backoff). ADR-0003, ADR-0006, ADR-0007, ADR-0008, ADR-0009.
