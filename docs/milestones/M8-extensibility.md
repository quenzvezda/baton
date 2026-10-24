# M8 — Extensibility

**Phase:** 3 · **Changes:** to create (likely one per item) · **Depends on:** G2

## Scope

| ID | Requirement |
| --- | --- |
| AUT-12 | Run now can override parameters and priority for one run |
| WF-11 | Definitions written as JSON/YAML referencing registered handlers, edited in the UI |
| ORC-14 | Named resource pools (e.g. `erp-api: 4 slots`) capping concurrent tasks across groups |
| — | Event triggers (not yet specified — needs a proposal) |

## ADRs / open questions

WF-11 partially supersedes ADR-0004 → new ADR required. Resource pools extend ADR-0009 (`pool:{name}` keys).

## Exit criteria

- [ ] Each change created, validated, implemented and archived
