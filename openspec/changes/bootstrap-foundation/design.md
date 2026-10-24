# Design

## Context

See proposal.md. The skeleton was generated at project init without compiling (no SDK in the init
environment), so the first job is to restore, build and fix. Constraints: ADR-0003, ADR-0007 (EF
migrations own the schema), ADR-0010, ADR-0011.

## Goals / Non-Goals

**Goals:** reproducible build on .NET 10; schema identical to `0001_baseline.sql` (already verified to
apply cleanly on PostgreSQL 16); one-command local database; CI as the merge gate.

**Non-Goals:** any product behavior; production deployment manifests (decided with OQ-06).

## Decisions

- **Schema ownership.** EF Core migrations are the only way the schema changes. Features EF cannot model
  (partial indexes with predicates on text columns are fine, but partitioning, `fillfactor`, autovacuum
  storage parameters, `lease_token_seq` usage) go into the migration with `migrationBuilder.Sql(...)`.
  `Schema/0001_baseline.sql` stays as the reviewed reference and is compared by a test.
- **History tables in EF.** Partitioned tables are created by raw SQL and mapped read-only for queries;
  inserts on the hot path use Npgsql.
- **Partition maintenance.** Prefer a small in-process job (orchestrator singleton loop, daily) that
  creates the next 3 monthly partitions and drops those older than 90 days — avoids a `pg_partman`
  extension dependency on managed Postgres. Switch to `pg_partman` if the platform provides it.
- **Solution file.** Keep `Baton.sln` (what the design spec names); `dotnet sln migrate` to `.slnx` is a
  one-line follow-up if the team prefers the .NET 10 default.
- **Connections.** One pooled `NpgsqlDataSource` per process; the orchestrator adds one non-pooled
  connection for `LISTEN` (PgBouncer transaction mode breaks `LISTEN`).

## Risks / Trade-offs

- [Pinned package versions are stale or wrong] → task 1.2 bumps and verifies restore first.
- [Schema drift between migration and reference] → `Schema_matches_baseline` test in CI.
