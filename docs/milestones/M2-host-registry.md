# M2 — Host registry, agent protocol & access control

**Phase:** 1 · **Change:** `openspec/changes/add-host-registry` · **Depends on:** M0 (parallel with M1)

## Goal

The platform knows its hosts — group, labels, capacity, builds, liveness — agents authenticate, and
people sign in with roles.

## Scope

HST-01, HST-02, HST-03, HST-04, HST-05, HST-06 (API), NFR-Security (OIDC + roles, agent credentials).

## ADRs / open questions

ADR-0005 (pull-based gRPC), ADR-0009 (limit rows), ADR-0013 (agent auth, Accepted), ADR-0014 (OIDC for people, Accepted).
OQ-05 (multi-group hosts), OQ-06 (where agents run).

## Exit criteria

- [x] ADR-0013 Accepted; OQ-05 and OQ-06 answered
- [x] An agent registers, heartbeats, shows Online, and goes Offline 30 s after it stops
- [x] Drain/undrain and group move work end to end
- [x] Anonymous 401, Viewer 403 on writes, Admin-only group management — covered by API tests
- [x] Every Control Plane endpoint has an authorization policy (inventory test)
- [ ] Change `add-host-registry` archived
