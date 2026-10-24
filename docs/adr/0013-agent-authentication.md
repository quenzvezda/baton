# ADR-0013: Agent authentication

- **Status:** Proposed
- **Date:** 2026-10-24
- **Deciders:** @Ripan (pending security review)
- **Spec:** §3 Security ("agents use mTLS or per-host tokens")

## Context

Agents claim work and report results; a rogue agent could steal tasks or fake results within a group.
The spec allows either mTLS or per-host tokens. Choice depends on where agents run (OQ-06).

## Decision (proposed)

Support **per-host bearer tokens** in v1 (issued by an Admin, stored hashed, scoped to one host group,
revocable), validated on every gRPC call. Keep the door open for **mTLS** (client certificate → host
identity) when agents run on infrastructure with a PKI. To be accepted before M2 closes.

## Consequences

**Positive**
- Works on VMs, containers and Kubernetes without a PKI; simple rotation.

**Negative / risks**
- Token leakage → tokens scoped to a group, hashed at rest, rotated; TLS required on the channel.

## Alternatives considered

| Option | Why not (yet) |
| --- | --- |
| mTLS only | Needs certificate issuance/rotation infrastructure on every host. |
| No agent auth inside a private network | Violates the security NFR. |
