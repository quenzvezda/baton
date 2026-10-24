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

## Risks / Trade-offs

- [Serializer upgrade changes canonical JSON] → golden-file tests; serializer settings frozen in one place.
- [Large definition assemblies slow startup] → validation is O(V+E); measure with 100 definitions.

## Open Questions

- Exact platform bounds (max attempts 10, max timeout 24 h) are proposals; confirm with OQ-02.
