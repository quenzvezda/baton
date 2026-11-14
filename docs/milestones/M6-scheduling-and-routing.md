# M6 — Scheduling & routing controls

**Phase:** 2 · **Change:** `add-scheduling-and-routing-controls` · **Depends on:** G1

## Goal

Finer control over when and where work runs.

## Scope

| ID | Requirement |
| --- | --- |
| AUT-08 | Cron misfire policy after downtime: FireOnce, Skip, CatchUp (up to N) |
| AUT-10 | Editor previews the next 5 fire times of each cron trigger |
| ORC-06 | Priority aging (+1 per 5 min, cap +20) via `AgingLoop` (30 s, singleton) |
| ORC-12 | Placement mode PerTask / PinnedRun |
| ORC-13 | Pause and resume dispatch per host group |

## ADRs / open questions

OQ-03 (PinnedRun importance), OQ-04 (re-pin vs fail when the pinned host dies).

## Exit criteria

- [x] Change created, validated, implemented and archived (`2026-11-14-add-scheduling-and-routing-controls`, PR #9)
- [x] Aging keeps the dispatch index usable (only changed rows updated) — measured under G1 load profile ([report](../reports/m6-aging-load.md): exactly backlog × cap writes, p95 Queued→Leased 14 ms)
- [x] OQ-04 answered: the run fails with "pinned host lost" (change design.md, 2026-11-14)
