# Design

## Context

See proposal.md. Constraints: ADR-0005 (pull-based gRPC), ADR-0009 (limit rows), ADR-0013 (agent auth,
Accepted 2026-10-25), ADR-0014 (people sign in with OIDC bearer tokens). Contract: `src/Baton.Agent.Protocol/Protos/agent.proto`.

## Goals / Non-Goals

**Goals:** a host appears, stays Online while heartbeating, goes Offline 30 s after the last heartbeat
without any orchestrator-local state; any orchestrator replica can serve any agent.

**Non-Goals:** lease renewal semantics (M3 completes Heartbeat), autoscaling.

## Decisions

- **Identity.** Host name is unique; re-registration with the same name updates the existing row
  (labels, slots, version, hashes) and keeps the host id. Registration requires a valid agent token
  scoped to the target group (ADR-0013).
- **Offline detection.** Status is derived lazily: a host is Offline when `now() - last_heartbeat_at > 30 s`.
  A periodic sweep (part of the reaper in M3) persists `status='offline'` for the hosts page and events;
  dispatch eligibility always uses the timestamp, never only the stored status.
- **Drain.** `draining` is set by operators via API; the agent learns it from the Heartbeat reply and
  stops claiming. Undrain returns to Online on the next heartbeat.
- **Group move.** Changing `host_group_id` takes effect on the next claim because claims read the group
  from the host row inside the claim transaction.
- **Limits.** Creating/updating a group upserts `group-runs:{id}` / `group-tasks:{id}` rows; `NULL` limit
  deletes the row (unlimited). Lowering a limit below `in_use` is allowed; new work waits until it drains.
- **Agent resilience.** `Microsoft.Extensions.Http.Resilience` for retries with jitter on transient gRPC errors.

## Decisions agreed with the owner (2026-10-25)

1. **Agent credentials are per host group** (ADR-0013, accepted). An Admin issues any number of named
   tokens per group and revokes each one separately. A token is 32 random bytes, base64url, prefixed
   `baton_`; it is shown once and only its SHA-256 is stored (`agent_credentials.token_hash`). Every
   gRPC call is checked against the database, with no cache, so revocation takes effect on the next
   call. TLS is required on the channel; the agent refuses a plaintext URL unless it is loopback or
   `Agent:AllowInsecureChannel` is set (development only).
2. **OQ-06 (where agents run)**: any of VMs, containers or Kubernetes. Nothing may assume a PKI or a
   stable host name, which is why tokens are per group (1) and offline hosts can be removed (8).
3. **OQ-05**: a host belongs to exactly one group in v1 (HST-05).
4. **People sign in through any OIDC provider** (ADR-0014). The API is a JWT bearer resource server
   configured by the standard `Authentication:Schemes:Bearer` section (`Authority`, `ValidAudiences`).
   `Baton:Auth:RoleClaimType` (default `roles`) and `Baton:Auth:Roles:{Viewer|Operator|Admin}` map IdP
   claim values to Baton roles. Roles are hierarchical: Admin ⊇ Operator ⊇ Viewer. Tests sign tokens
   with a local key; local development uses `dotnet user-jwts`. The web console's sign-in flow is M5.
5. **Drain is an operator intent, stored apart from liveness**: `hosts.drain_requested`. The visible
   status is derived: Offline when the heartbeat is older than 30 s, else Draining when drain was
   requested or the agent reports it is shutting down, else Online. A drained host that goes offline
   and comes back is still draining.
6. **Protocol additions** (additive, `baton.agent.v1`): `RegisterReply.draining = 4` and
   `HeartbeatReply.draining = 3` tell the agent it is drained.
7. **Group move vs. group tokens.** A host stores the credential it registered with
   (`hosts.credential_id`). A token may act for a host when its group is the host's current group or
   the group of the host's bound credential, so a host an Admin moved keeps working with its old
   token, and a token of another group can neither hijack nor heartbeat for it. Re-registration
   never changes the group: an Admin move is durable. Moves use `PUT /api/hosts/{id}/group` (Admin).
8. **REST additions**: `DELETE /api/host-groups/{id}`; `GET|POST /api/host-groups/{id}/agent-tokens`
   and `DELETE /api/host-groups/{id}/agent-tokens/{tokenId}` (revoke); `DELETE /api/hosts/{id}` for an
   Offline host only. Viewers may read groups (`GET /api/host-groups`), matching "Viewer may read".
9. **No offline sweep in M2.** Inventory and eligibility derive status from `now() - last_heartbeat_at`
   on every read. Persisting `status='offline'` is part of the reaper in M3. Tests age
   `last_heartbeat_at` in the database instead of sleeping.

## Implementation notes (M2)

- Register, Heartbeat and credential checks are hand-written SQL in `Baton.Persistence/Queue`
  (ADR-0007); group, credential and host administration in the API use EF Core.
- Registration is one `INSERT … ON CONFLICT (name) DO UPDATE … WHERE <token may act for the host>`,
  so concurrent registrations of one name end in one row.
- Deleting a group deletes its credentials and limit rows in the same transaction;
  `hosts.credential_id` is `ON DELETE SET NULL`.
- Group names are 1–63 characters of `[a-z0-9-]`, starting with a letter or digit; limits are ≥ 1
  when given; `lease_seconds` is 30–3600 (default 60).
- The agent's lifecycle (registering → online ⇄ draining → stopping) is a pure state machine in
  `Baton.Domain/Hosts`, unit-tested; the worker in `Baton.Agent` drives it.
- Retries use a Polly pipeline from `Microsoft.Extensions.Resilience` (via
  `Microsoft.Extensions.Http.Resilience`): exponential backoff with jitter on transient gRPC status
  codes; registration retries any failure (unknown group, bad token) with the same backoff.

## Risks / Trade-offs

- [Clock-free liveness relies on DB time] → heartbeat writes `now()` server-side; agent clocks ignored.
- [Deleting a group in use] → rejected while hosts or automations reference it.
