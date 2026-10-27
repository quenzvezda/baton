# M3 — Task dispatch & leases

**Phase:** 1 · **Change:** `openspec/changes/add-task-dispatch` · **Depends on:** M1, M2

## Goal

Ready tasks reach eligible hosts in priority order under fenced leases; crashes, stalls and late
results never corrupt state. **Critical path of phase 1.**

## Scope

ORC-03, ORC-04, ORC-05, ORC-07, ORC-08, ORC-09, ORC-10, WF-05 (timeout enforcement),
NFR-DispatchLatency, NFR-ExecutionGuarantee, NFR-Auditability (attempts, task events).

## ADRs / open questions

ADR-0003, ADR-0006, ADR-0007, ADR-0008, ADR-0009. OQ-02 (task duration ⇒ lease tuning),
**OQ-03 must be answered before M3 closes** (may pull PinnedRun into phase 1).

## Exit criteria

- [x] Claim race test: 8 claimers × 1,000 tasks ⇒ each task leased exactly once
- [x] Stale-token Start/Complete/AppendLogs rejected with no state change
- [x] Chaos test: agent killed mid-task ⇒ task retried elsewhere, run completes
- [x] p95 Queued→Leased < 2 s with a waiting agent; 50 claims/s for 60 s without errors
- [x] Attempt history shows lost/timed-out/succeeded attempts with hosts and tokens
- [x] OQ-03 answered and ROADMAP updated if PinnedRun moves
- [x] Change `add-task-dispatch` archived
