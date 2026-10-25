# ADR-0013: Agent authentication

- **Status:** Accepted
- **Date:** 2026-10-25 (proposed 2026-10-24)
- **Deciders:** @Ripan (owner review of M2, 2026-10-25)
- **Spec:** §3 Security ("agents use mTLS or per-host tokens") · NFR-Security · OpenSpec change `add-host-registry`

## Context

Agents claim work and report results; a rogue agent could steal tasks or fake results within a group.
The spec allows either mTLS or per-host tokens. Agents may run on VMs, containers or Kubernetes (OQ-06,
answered: any of them), so neither a PKI nor stable host names can be assumed.

## Decision

Agents authenticate with **bearer tokens scoped to one host group**, validated on every gRPC call.

- An Admin issues any number of named tokens per group through the Control Plane API and revokes each
  separately. A token is 32 random bytes, base64url-encoded with a `baton_` prefix (easy to spot by
  secret scanners). It is shown once; only its SHA-256 hash is stored (`agent_credentials`).
- The orchestrator looks the hash up on every call, with no cache, so a revoked token fails its next
  call. A missing, unknown or revoked token is `UNAUTHENTICATED`; a valid token used for another group
  is `PERMISSION_DENIED`.
- A host records the credential it registered with. A token may act for a host when its group is the
  host's current group or the group of that bound credential, so an Admin can move a host between
  groups (HST-05) without re-issuing its token, and a token of another group cannot take the host over.
- TLS is required on the channel. The agent refuses a plaintext orchestrator URL unless it is loopback
  or insecure channels are explicitly allowed (development).

mTLS (client certificate → host identity) stays possible later, as an additional authenticator, when
agents run on infrastructure with a PKI.

## Consequences

**Positive**
- Works on VMs, containers and Kubernetes without a PKI; one token per group suits autoscaled agents.
- Rotation is issue-new, roll out, revoke-old; revocation is immediate.

**Negative / risks**
- Token leakage lets the holder act as any host of that group → tokens are scoped to one group,
  hashed at rest, revocable, and TLS is required. Fencing (ADR-0006) still stops stale writes.
- One database lookup per agent call → a primary-key-sized unique index on the hash; agents call about
  once per 10 s each.

## Alternatives considered

| Option | Why not (yet) |
| --- | --- |
| mTLS only | Needs certificate issuance/rotation infrastructure on every host. |
| One token per host | Hosts in containers/Kubernetes are ephemeral; issuing a token per pod needs automation we do not have. |
| Cached token validation | Revocation would lag by the cache lifetime for a negligible saving. |
| No agent auth inside a private network | Violates the security NFR. |
