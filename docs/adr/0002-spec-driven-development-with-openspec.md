# ADR-0002: Spec-driven development with OpenSpec

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §2 (requirement IDs), §10 (roadmap)

## Context

The design spec lists 49 requirements (AUT, WF, ORC, HST, OBS) with MoSCoW priorities. Work will be
done by people and AI coding agents. We need behavior to be agreed **before** code, reviewable in PRs,
and traceable back to requirement IDs — and a current, accurate statement of what the system does.

## Decision

Use [OpenSpec](https://github.com/Fission-AI/OpenSpec) (CLI pinned to `@fission-ai/openspec@1.14.1`,
schema `spec-driven`) in `openspec/`:

- `openspec/specs/<capability>/spec.md` — the **current truth**: behavior that is built and shipped.
- `openspec/changes/<change-id>/` — proposals in flight: `proposal.md`, `design.md`, `tasks.md` and
  delta specs (`## ADDED|MODIFIED|REMOVED Requirements`).
- A change is archived (`openspec archive <id>`) when it ships; its deltas merge into `specs/`.

Conventions:
- Each roadmap milestone maps to one OpenSpec change (see `ROADMAP.md`). Phase-1 changes are pre-written.
- Every requirement header ends with its spec ID, e.g. `### Requirement: Fenced results (ORC-08)`.
- Specs describe observable behavior; implementation choices go to `design.md`; cross-cutting
  architectural choices go to an ADR, and `design.md` links it.
- CI runs `openspec validate --all --strict`.

## Consequences

**Positive**
- Clear contract per milestone; reviewers approve behavior before implementation.
- `openspec/specs` stays an accurate description of the shipped system.
- AI agents get explicit workflows (`.agents/skills/openspec-*`).

**Negative / risks**
- `openspec/specs/` is empty until the first change is archived → that is expected; the design spec
  remains the reference until then.
- Tool churn (OpenSpec releases often) → version pinned in CI; bump deliberately.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Design spec only + issues | No per-change behavior contract, no "current truth" document. |
| GitHub Spec Kit | Comparable; OpenSpec's delta/archive model fits a brownfield-over-time codebase better. |
