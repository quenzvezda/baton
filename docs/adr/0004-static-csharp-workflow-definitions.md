# ADR-0004: Static C# workflow definitions with immutable `key@version`

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §5, WF-01, WF-03, WF-04, WF-11

## Context

Workflows bind tasks to C# handler classes that must ship with the agents anyway. Runtime authoring
(JSON/YAML) is wanted eventually (WF-11, phase 3) but would require a handler registry, an editor and
a validation surface before v1 can ship. During rolling deploys, old and new agent builds coexist.

## Decision

- Definitions are C# classes (`WorkflowDefinition<TParams>`) in `Baton.Definitions`, declared with a
  fluent builder, shipped as a NuGet package referenced by API, orchestrator and agents.
- At API startup every definition is validated (acyclic via Kahn, unique kebab-case keys ≤ 64 chars,
  resolvable handlers, serializable params with a JSON Schema, retry/timeout bounds). Any failure
  stops startup.
- The builder output is serialized to canonical JSON (tasks sorted by key) and hashed with SHA-256.
  `(key, version, hash, graph, params_schema)` is upserted; the same `key@version` with a different
  hash is a startup error.
- Runs record the definition id; agents report the hashes they carry; dispatch matches on hash.

## Consequences

**Positive**
- Compile-time safety and refactoring support for definitions; no runtime authoring surface in v1.
- Safe rolling deploys: old runs finish on old builds, new runs go to new builds.

**Negative / risks**
- Any graph change needs a version bump and a release → acceptable for v1; WF-11 revisits in phase 3.
- Canonical JSON must be stable across builds → golden-file tests on the serializer (M1).

## Alternatives considered

| Option | Why not |
| --- | --- |
| JSON/YAML definitions in v1 | Larger v1 scope (editor, handler registry, validation UX). Deferred to phase 3. |
| Unversioned definitions | In-flight runs would change shape mid-run; no safe rolling deploy. |
