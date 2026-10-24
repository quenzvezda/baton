# ADR-0005: Pull-based agents over gRPC long-poll

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §4, §8 "Agent", §9 gRPC, HST-02, HST-03

## Context

Hosts may sit behind firewalls/NAT and should join a group without orchestrator changes. Dispatch
latency target is p95 < 2 s from Queued to Leased when a slot is free.

## Decision

Agents **pull** work: they call `Claim` with their free slot count over gRPC (HTTP/2) and the
orchestrator long-polls up to 20 s, waking on `NOTIFY tasks_ready`. Agents register on start
(`Register`), renew all leases with one `Heartbeat` every 10 s, stream logs (`AppendLogs`), and
report `Start`/`Complete` fenced by lease token. Contract: `Baton.Agent.Protocol/Protos/agent.proto`
(`baton.agent.v1`). Nothing ever pushes to an agent.

## Consequences

**Positive**
- Agents need outbound access only; any orchestrator replica can serve any agent.
- Back-pressure is natural: an agent never claims more than its free slots.

**Negative / risks**
- Many idle long-polls hold connections → bounded by host count; tune Kestrel limits.
- Breaking proto changes need a versioned package (`v2`) and a dual-serving window.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Push dispatch from orchestrator | Requires inbound connectivity and per-agent connection state. |
| REST polling | Higher latency or more load; no streaming for logs. |
| Broker-delivered tasks | See ADR-0003. |
