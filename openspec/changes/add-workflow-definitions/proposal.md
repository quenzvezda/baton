# Proposal

## Why

Milestone **M1 — Workflow definitions**. Every run executes a definition, so the definition model —
how workflows are declared, validated, versioned and handed data — is the foundation the orchestrator,
agents and UI build on. Requirements: WF-01..WF-08, plus the catalog API part of WF-09.

## What Changes

- Public SDK (`Baton.Abstractions`): definition base class, fluent builder, task handler interface,
  task context with idempotency key and ancestor-only output access, trigger rules, non-retryable failure.
- Builder implementation and DAG validation in `Baton.Domain` (acyclic, keys, handlers, params schema, bounds).
- Canonical graph JSON + SHA-256 hash; registration into the catalog at Control Plane API startup;
  startup fails on any invalid definition or on a changed graph under an existing `key@version`.
- `services.AddWorkflows(assembly)` registration helper.
- Read-only catalog API: `GET /api/workflows`, `GET /api/workflows/{key}/versions/{version}`.

Out of scope: catalog **page** (M5), re-run from failed tasks WF-10 (M7), JSON/YAML definitions WF-11 (M8).

## Capabilities

### New Capabilities

- `workflow-definitions`: how workflows are declared, validated, versioned, registered and read from the catalog, and what data a task receives and returns.

### Modified Capabilities

None.

## Impact

`Baton.Abstractions`, `Baton.Domain`, `Baton.Definitions`, `Baton.Persistence` (`workflow_definitions`),
`Baton.ControlPlane.Api` (startup + 2 endpoints). ADR-0004.
