# ADR-0001: Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** —

## Context

Baton's design spec (2026-10-22) makes a number of load-bearing choices — Postgres as the only broker,
leases with fencing, static C# definitions — and leaves others open. Without a durable record, the
reasoning gets lost and decisions are relitigated, or quietly reversed by a refactor.

## Decision

Keep Architecture Decision Records in `docs/adr/`, one Markdown file per decision, using a light MADR
format (`template.md`). Number sequentially; never renumber or delete. An accepted ADR is immutable —
change course with a new ADR that supersedes it.

Write an ADR when a change: adds or replaces infrastructure or a major dependency; changes a
cross-cutting invariant (consistency, delivery guarantee, identity, security); changes the
deployment topology; or settles one of the open questions in `docs/open-questions.md`.

## Consequences

**Positive**
- New team members and AI agents can read why, not just what (AGENTS.md points here).
- Open questions get an explicit home and owner.

**Negative / risks**
- Some overhead per decision → keep ADRs short (one page) and skip them for local, reversible choices.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Decisions only in the design spec | The spec is a snapshot; it does not track supersession over time. |
| Wiki pages | Drifts from the code; not reviewed in PRs. |
