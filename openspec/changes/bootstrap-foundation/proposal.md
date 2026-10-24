# Proposal

## Why

Milestone **M0 — Foundation**. Every later milestone needs a building solution, a migrated database,
a Postgres test harness and CI. Doing this first, with no behavior, keeps the feature changes focused
on behavior.

## What Changes

- Solution skeleton (`Baton.sln`, 9 src + 2 test projects), central package management, `.editorconfig`,
  `global.json` pinned to the .NET 10 SDK. *(Scaffolded at init; this change verifies and hardens it.)*
- EF Core initial migration that reproduces `src/Baton.Persistence/Schema/0001_baseline.sql`
  (11 tables, partial indexes, `lease_token_seq`, fillfactor/autovacuum on `task_runs`, monthly
  partitions for `task_attempts`, `task_logs`, `run_events`).
- Partition maintenance: create partitions 3 months ahead (pg_partman vs. small job decided here, ADR-0011).
- `NpgsqlDataSource` registration and connection-string conventions (pooled + dedicated LISTEN connection).
- Testcontainers fixture applies migrations; schema assertion test.
- OpenTelemetry baseline wiring (traces/metrics/logs exporters configurable, off by default).
- CI: build, unit + integration tests, `openspec validate --all --strict`; `TreatWarningsAsErrors` on.
- Local dev: `docker compose up -d` Postgres 16.

Out of scope: any requirement-level behavior (M1+).

## Capabilities

### New Capabilities

None — tooling and scaffolding only (`skip_specs: true`).

### Modified Capabilities

None.

## Impact

All projects; `.github/workflows/ci.yml`; `docker-compose.yml`. No runtime behavior yet.
