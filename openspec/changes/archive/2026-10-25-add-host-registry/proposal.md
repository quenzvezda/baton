# Proposal

## Why

Milestone **M2 — Host registry & agent protocol**. Tasks can only be dispatched to hosts the platform
knows about: which group they belong to, what labels and capacity they have, which definition builds
they carry and whether they are alive. Requirements: HST-01..HST-05, the inventory API part of HST-06,
and NFR-Security (people and agents). Host-group administration is the first Admin-only API, so
sign-in and roles land here too.

## What Changes

- Host-group administration API with run and task limits (`/api/host-groups`), backed by limit rows.
- gRPC `AgentService.Register` and `Heartbeat` (lease renewal is stubbed until M3), agent authentication.
- Liveness: Offline after 30 s without heartbeat; drain/undrain; single-group membership.
- `Baton.Agent` skeleton: options, `AgentClient` with resilience, register + heartbeat loop, graceful drain on shutdown.
- Host inventory API `GET /api/hosts`.
- OIDC sign-in for people with Viewer / Operator / Admin roles, applied to every Control Plane endpoint
  (including the M1 catalog endpoints).

Out of scope: claiming and executing tasks (M3), pausing group dispatch ORC-13 (M6), hosts **page** (M5),
multi-group hosts (OQ-05).

## Capabilities

### New Capabilities

- `host-registry`: host groups and their limits, agent registration and authentication, liveness, draining and the host inventory.
- `access-control`: how people authenticate and which role may do what in the control plane.

### Modified Capabilities

None.

## Impact

`Baton.Agent.Protocol`, `Baton.Orchestrator` (AgentService), `Baton.Agent`, `Baton.ControlPlane.Api`,
`Baton.Persistence` (`host_groups`, `hosts`, `concurrency_limits`). ADR-0005, ADR-0013 and ADR-0014 (accepted 2026-10-25). New table `agent_credentials`; additive
`agent.proto` fields; REST additions listed in design.md.
