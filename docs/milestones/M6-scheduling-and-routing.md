# M6 — Scheduling & routing controls

**Phase:** 2 · **Change:** `add-scheduling-and-routing-controls` *(create when starting)* · **Depends on:** G1

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

- [ ] Change created, validated, implemented and archived
- [ ] Aging keeps the dispatch index usable (only changed rows updated) — measured under G1 load profile
- [ ] OQ-04 answered
