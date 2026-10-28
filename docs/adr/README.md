# Architecture Decision Records

ADRs record **why** the architecture is the way it is. Behavior (**what** the system does) lives in
OpenSpec (`openspec/specs`, `openspec/changes`) — see ADR-0002 for how the two fit together.

## Process

1. Copy `template.md` to `NNNN-kebab-title.md` (next free number, never reused).
2. Open it as **Proposed** in a PR; link the OpenSpec change or milestone it unblocks.
3. Merge as **Accepted** once reviewed. ADRs are immutable after acceptance — to change a
   decision, write a new ADR and mark the old one **Superseded by ADR-NNNN**.

Statuses: `Proposed` · `Accepted` · `Rejected` · `Deprecated` · `Superseded by ADR-NNNN`

## Index

| # | Title | Status | Spec |
| --- | --- | --- | --- |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted | — |
| [0002](0002-spec-driven-development-with-openspec.md) | Spec-driven development with OpenSpec | Accepted | — |
| [0003](0003-postgresql-as-single-source-of-truth.md) | PostgreSQL as the single source of truth and work queue | Accepted | §1, §4, §7 |
| [0004](0004-static-csharp-workflow-definitions.md) | Static C# workflow definitions, immutable `key@version` | Accepted | §5 |
| [0005](0005-pull-based-agents-over-grpc.md) | Pull-based agents over gRPC long-poll | Accepted | §4, §9 |
| [0006](0006-at-least-once-execution-leases-and-fencing.md) | At-least-once execution with leases and fencing tokens | Accepted | §3, §6 |
| [0007](0007-hand-written-sql-on-the-hot-path.md) | Hand-written SQL on the hot path, EF Core elsewhere | Accepted | §7, §8 |
| [0008](0008-replica-coordination-in-postgres.md) | Replica coordination with SKIP LOCKED and advisory locks | Accepted | §6, §7, §8 |
| [0009](0009-concurrency-limits-as-counter-rows.md) | Concurrency limits as transactional counter rows | Accepted | §6, §7 |
| [0010](0010-uuidv7-identifiers.md) | Time-ordered UUIDv7 identifiers | Accepted | §7, §8 |
| [0011](0011-history-partitioning-and-retention.md) | Monthly partitions and 90-day retention for history | Accepted | §3, §7 |
| [0012](0012-web-ui-framework.md) | Web UI framework | Accepted | §4, OQ-09 |
| [0013](0013-agent-authentication.md) | Agent authentication with group-scoped bearer tokens | Accepted | §3 |
| [0014](0014-people-authentication-with-oidc.md) | People authenticate with OIDC bearer tokens | Accepted | §3 |
| [0015](0015-webhook-secrets-with-data-protection.md) | Webhook secrets encrypted with ASP.NET Core Data Protection | Accepted | §3, AUT-04 |
| [0016](0016-json-schema-validation-with-jsonschema-net.md) | Validate automation parameters with JsonSchema.Net | Accepted | AUT-03 |
| [0017](0017-keycloak-as-development-identity-provider.md) | Keycloak as the local development identity provider | Accepted | §3 |
