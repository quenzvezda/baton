# M4 — Automations, triggers & run lifecycle

**Phase:** 1 · **Change:** `openspec/changes/add-automations-and-run-lifecycle` · **Depends on:** M3 (CRUD can start after M1)

## Goal

Users configure automations; cron, Run now and webhooks create exactly one run per firing; runs are
admitted under limits, finish with the right status, time out and can be cancelled. **v1 backend complete.**

## Scope

AUT-01, AUT-02, AUT-03 (validation), AUT-04, AUT-05, AUT-06, AUT-07, AUT-09, ORC-01, ORC-02, ORC-11,
NFR-TriggerAccuracy, NFR-Auditability (run events).

## ADRs / open questions

ADR-0008, ADR-0009. OQ-07 (secret store) for the parameter guidance.

## Exit criteria

- [x] Two orchestrator replicas never create two runs for the same fire time
- [x] One integration test per concurrency policy (Skip, Queue, Replace, Allow)
- [x] Admission: group-full, pass-over and no-deadlock-across-replicas tests green
- [x] Cancellation scenarios (running, unresponsive host, already finished) green
- [x] E2E: cron automation runs the example workflow on two agents to Succeeded
- [x] OQ-07 answered and documented for definition authors
- [x] Change `add-automations-and-run-lifecycle` archived
