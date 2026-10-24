# Design

## Context

See proposal.md. Constraints: ADR-0004 (static C# definitions, immutable `key@version`), ADR-0006
(at-least-once ⇒ idempotency key). Reference shapes: design spec §5 "Abstractions" and "Example definition".

## Goals / Non-Goals

**Goals:** deterministic graph hash across builds and machines; fail fast at startup; zero reflection on
the per-attempt hot path after startup (cache compiled invokers).

**Non-Goals:** runtime authoring; executing tasks (M3); UI rendering (M5).

## Decisions

- **Graph model.** Builder produces an immutable `WorkflowGraph` (tasks keyed by task key, edges,
  per-task options, params type). Validation runs on the graph, not on builder state.
- **Cycle detection.** Kahn's topological sort; report the remaining nodes as the cycle in the error.
- **Canonical JSON.** `System.Text.Json` with a fixed contract: tasks sorted ordinally by key, `dependsOn`
  sorted, options always emitted with explicit defaults, invariant culture, no indentation. Handler is
  recorded by full type name. Golden-file tests lock the format; any format change is a hash change
  and therefore needs a version bump policy (documented in Definitions README).
- **Params schema.** `JsonSchemaExporter` on the params type with the same serializer options used at
  runtime; schema stored in `params_schema`.
- **Registration.** Upsert in one transaction at API startup: insert if `key@version` absent; no-op if
  hash equal; throw (startup error) if hash differs. Old versions are never modified.
- **Output size.** Enforced where the output is serialized (agent) and again on Complete (server);
  > 256 KB fails the attempt as non-retryable because the output is deterministic.
- **Handler invocation.** Resolve handler from a DI scope per attempt; cache a compiled delegate per
  `(definition hash, task key)`.
- **Default timeout.** A task without `.Timeout(...)` gets **1 hour** (decided 2026-10-24). Defaults are
  written into the canonical JSON, so changing any default later changes the hash of every definition
  that relies on it and fails their registration — treat the defaults as frozen.
- **Platform bounds.** At most **10 attempts** and a timeout of at most **24 hours** per task (decided
  2026-10-24, matching the WF-03 scenarios). Bounds are checked at validation and are not part of the
  hash, so they can change later without re-versioning. OQ-02 stays open for lease tuning (M3).
- **Display name.** `WorkflowDefinition<TParams>.DisplayName` is `virtual`; the default is derived from
  the key (`nightly-sales-report` → "Nightly sales report"). It is a label, not part of the graph: it is
  left out of the canonical JSON and hash, and registration refreshes `display_name` on an existing
  `key@version` without treating it as a changed definition (decided 2026-10-24).
- **Placement.** Builder, validation, canonical JSON, hashing and `AddWorkflows` live in `Baton.Domain`
  (`AddWorkflows` needs only `Microsoft.Extensions.DependencyInjection.Abstractions`, no I/O). Params
  and outputs use `JsonSerializerDefaults.Web` (camelCase), as in the AUT-03 scenario.

- **Implementation notes (M1).**
  - The canonical JSON carries `key`, `version`, the params type name and the tasks; the params
    *schema* is stored beside it but not hashed (exporter output may vary across .NET versions).
  - Types are written by namespace-qualified name without assembly versions, so package bumps do not
    change hashes; renaming a handler or params type does.
  - `JsonSchemaExporter` does not throw for unsupported members; it marks them
    (`"$comment": "Unsupported .NET type"`), and validation fails on any such marker.
  - Registration is `INSERT … ON CONFLICT (key, version) DO UPDATE SET display_name … WHERE hashes are
    equal`; no row back means a changed graph, and the whole batch rolls back.
  - WF-08 is decided by the pure `RetryDecision.AfterFailure` in `Baton.Domain/Retries`, which the M3
    executor uses; outputs over 256 KB raise `NonRetryableException` from `TaskOutput`.
  - Catalog API tests host the endpoints on Kestrel (port 0) instead of `Mvc.Testing`, which no ADR
    or central package covers yet.

## Risks / Trade-offs

- [Serializer upgrade changes canonical JSON] → golden-file tests; serializer settings frozen in one place.
- [Large definition assemblies slow startup] → validation is O(V+E); measure with 100 definitions.

## Open Questions

- None for M1. Platform bounds were settled above; OQ-02 (task duration) still drives lease tuning in M3.
