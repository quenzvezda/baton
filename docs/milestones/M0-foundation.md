# M0 — Foundation

**Phase:** 1 · **Change:** `openspec/changes/bootstrap-foundation` · **Depends on:** —

## Goal

A solution that builds, a database that migrates, a Postgres test harness and CI — so that M1 and M2
can start in parallel and only deal with behavior.

## Scope

- Solution skeleton verified on the .NET 10 SDK; package versions bumped; warnings as errors.
- EF Core initial migration reproducing `src/Baton.Persistence/Schema/0001_baseline.sql`.
- Monthly partition maintenance (ADR-0011): mechanism chosen and creating 3 months ahead.
- Testcontainers fixture applying migrations; OpenTelemetry baseline wiring.
- CI: build, tests, OpenSpec validation. GitHub milestones created.

## ADRs / open questions

ADR-0003, ADR-0007, ADR-0010, ADR-0011.

## Exit criteria

- [ ] `dotnet build -c Release` with zero warnings, `TreatWarningsAsErrors=true`
- [ ] `dotnet ef database update` succeeds on `docker compose` Postgres 16
- [ ] `Schema_matches_baseline` integration test green in CI
- [ ] Next 3 monthly partitions exist after migration
- [ ] CI green on `main` (build, unit, integration, `openspec validate --all --strict`)
- [ ] README "Getting started" verified on a clean clone
- [ ] Change `bootstrap-foundation` archived
