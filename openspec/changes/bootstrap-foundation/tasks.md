# Tasks

## 1. Solution and build

- [x] 1.1 Restore and build `Baton.sln` on .NET 10 SDK; verify `dotnet build -c Release` has zero warnings
- [x] 1.2 Run `dotnet list package --outdated`, bump pinned versions in `Directory.Packages.props`; verify restore succeeds
- [x] 1.3 Set `TreatWarningsAsErrors=true` in `Directory.Build.props`; verify CI build stays green
- [ ] 1.4 Decide `.sln` vs `.slnx` (`dotnet sln migrate`); verify the chosen file opens in VS/Rider and builds in CI

## 2. Database schema

- [x] 2.1 Add EF Core entity configurations for config tables (`host_groups`, `hosts`, `automations`, `automation_triggers`, `workflow_definitions`, `concurrency_limits`); verify model builds
- [x] 2.2 Create initial migration; raw SQL for partial indexes, `lease_token_seq`, partitioned tables, `fillfactor=80` and `autovacuum_vacuum_scale_factor=0.01` on `task_runs`; verify `dotnet ef database update` against `docker compose` Postgres
- [x] 2.3 Integration test `Schema_matches_baseline` compares migrated catalog (tables, indexes, constraints) to `0001_baseline.sql`; verify it passes
- [x] 2.4 Choose partition maintenance (pg_partman vs job), implement creating 3 months ahead; verify with an integration test that inserts into next month

## 3. Infrastructure wiring

- [x] 3.1 Register `NpgsqlDataSource` (pooled) and a dedicated non-pooled connection for `LISTEN` in Orchestrator; verify `/healthz` checks DB connectivity
- [x] 3.2 `PostgresFixture` applies migrations once per collection; verify `PostgresSmokeTests` and schema test pass in CI
- [x] 3.3 OpenTelemetry baseline (ASP.NET Core, HttpClient, Npgsql instrumentation; OTLP exporter behind config); verify traces appear in a local collector or console exporter

## 4. Developer experience

- [ ] 4.1 README "Getting started" runs end to end on a clean machine; verify by following it in a fresh clone
- [ ] 4.2 Create GitHub milestones with `scripts/create-github-milestones.sh`; verify M0–M8 exist on the repo

## Workflow follow-up

- Archive the change once CI is green on `main`.
