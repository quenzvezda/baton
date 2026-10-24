# M5 — Web console & run monitoring

**Phase:** 1 · **Change:** `openspec/changes/add-web-console-and-monitoring` · **Depends on:** M4 (UI shell can start after M2)

## Goal

People configure automations and understand runs entirely from the browser.

## Scope

OBS-01, OBS-02, OBS-03, WF-09 (page), HST-06 (page), AUT-03 (generated form), role-aware UI.

## ADRs / open questions

**ADR-0012 must be Accepted** (UI framework) — closes OQ-09. ADR-0011 (purged runs).

## Exit criteria

- [ ] ADR-0012 Accepted
- [ ] Runs list pages through 10k runs without duplicates; first page < 500 ms
- [ ] Live logs visible within 2 s
- [ ] Catalog DAG, editor form, run detail DAG and hosts page usable by a Viewer/Operator walkthrough
- [ ] Change `add-web-console-and-monitoring` archived ⇒ phase 1 complete
