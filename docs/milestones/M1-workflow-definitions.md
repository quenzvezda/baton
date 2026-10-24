# M1 — Workflow definitions

**Phase:** 1 · **Change:** `openspec/changes/add-workflow-definitions` · **Depends on:** M0

## Goal

Workflows can be written in C#, are validated and registered at startup with immutable versions, and
are readable through the catalog API.

## Scope

WF-01, WF-02, WF-03, WF-04, WF-05, WF-06, WF-07, WF-08, WF-09 (API), AUT-03 (schema export),
NFR-ExecutionGuarantee (idempotency key).

## ADRs / open questions

ADR-0004 (static definitions), ADR-0006 (idempotency). OQ-02 for platform bounds on attempts/timeouts.

## Exit criteria

- [x] The design-spec `nightly-sales-report` example compiles and registers
- [x] Every startup-validation scenario has a failing-startup test
- [x] Golden-file test locks the canonical JSON; reordering declarations keeps the hash
- [x] Re-registration paths (insert / no-op / changed hash fails) covered by integration tests
- [x] `GET /api/workflows` and `GET /api/workflows/{key}/versions/{version}` live
- [x] Change `add-workflow-definitions` archived ⇒ `openspec/specs/workflow-definitions` exists
