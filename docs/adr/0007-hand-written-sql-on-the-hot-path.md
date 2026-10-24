# ADR-0007: Hand-written SQL on the hot path, EF Core elsewhere

- **Status:** Accepted
- **Date:** 2026-10-24
- **Deciders:** @Ripan
- **Spec:** §7 "Hot-path queries", §8 "Packages"

## Context

Claim, heartbeat, complete and reaper queries rely on Postgres-specific features
(`FOR UPDATE SKIP LOCKED`, `UPDATE … FROM unnest(…)`, `RETURNING`, partial indexes, sequences) and
their exact shape determines throughput and correctness. CRUD on automations, groups and history is
ordinary and benefits from EF Core's migrations and change tracking.

## Decision

- Hot-path queue SQL lives in `Baton.Persistence/Queue` as hand-written SQL executed with Npgsql
  (+ Dapper for mapping) on `NpgsqlDataSource`.
- Everything else uses EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`) with migrations as the schema
  owner. Partitioning, partial indexes, fillfactor and storage parameters are applied in migrations via
  raw SQL. `Schema/0001_baseline.sql` is the reference the initial migration must match.

## Consequences

**Positive**
- Full control and reviewability of the queries that matter; EF productivity for the rest.

**Negative / risks**
- Two data-access styles → hot-path SQL requires an integration test per query, including concurrent
  cases (two claimers, stale token, expiry).
- Schema drift between EF model and raw SQL → an integration test asserts the migrated schema.

## Alternatives considered

| Option | Why not |
| --- | --- |
| EF Core everywhere | `SKIP LOCKED` and batched fenced updates are awkward/opaque through LINQ. |
| Raw SQL everywhere | Loses migrations and productivity for plain CRUD. |
