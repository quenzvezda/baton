# AGENTS.md

Instructions for AI coding agents (and humans) working in this repository. Read this file first,
then the milestone you are working on (`ROADMAP.md` → `docs/milestones/`).

## Project at a glance

**Baton** — workflow engine & automations platform. Users configure *automations* in a web UI; each one
triggers a *workflow* (a versioned DAG of C# tasks); an *orchestrator* routes tasks to *host groups* of
*agents* under limits, priorities and fenced leases. **PostgreSQL is the single source of truth** for
configuration, run state, the task queue and leases.

Stack: .NET 10 · ASP.NET Core minimal APIs · gRPC · EF Core + Npgsql (+ Dapper on the hot path) ·
PostgreSQL 16+ · Blazor Server · xUnit + Testcontainers.

Sources of truth, in order:
1. `openspec/specs/` — behavior that is built (one folder per capability, created by `archive`)
2. `openspec/changes/<id>/` — behavior being built (proposal, design, tasks, delta specs)
3. `docs/adr/` — why the architecture is the way it is
4. `docs/design/design-spec.md` — original design spec (2026-10-22); reference, not updated

## Repository map

```text
Baton.slnx
src/
  Baton.Abstractions/      public SDK: IWorkflowDefinition, builder, ITaskHandler, TaskContext
  Baton.Definitions/       workflows + handlers (NuGet package, referenced by API/orchestrator/agent)
  Baton.Domain/            pure logic: states, DAG validation, trigger rules, backoff, aging — no I/O
  Baton.Persistence/       EF Core DbContext + migrations; Queue/ = hand-written hot-path SQL
  Baton.Agent.Protocol/    agent.proto (gRPC contract, package baton.agent.v1)
  Baton.ControlPlane.Api/  REST API, webhooks, definition registration at startup
  Baton.Orchestrator/      BackgroundService loops + gRPC AgentService
  Baton.Agent/             Worker Service on each host
  Baton.Web/               Blazor Server console (talks to the API only)
  Shared/                  source files linked into server projects (OpenTelemetry baseline)
tests/
  Baton.Domain.Tests/      fast unit tests
  Baton.Agent.Tests/       agent worker and executor against a fake AgentService client
  Baton.Integration.Tests/ Testcontainers Postgres (requires Docker)
openspec/                  OpenSpec config, specs, changes
docs/adr/                  Architecture Decision Records
docs/milestones/           milestone + gate definitions with exit criteria
.agents/skills/            OpenSpec workflow skills (propose, apply, archive, …)
```

## Commands

```bash
docker compose up -d                                  # Postgres 16 on localhost:5432 (baton/baton)
dotnet restore Baton.slnx
dotnet build Baton.slnx -c Release
dotnet test tests/Baton.Domain.Tests                  # unit
dotnet test tests/Baton.Agent.Tests                   # unit (agent)
dotnet test tests/Baton.Integration.Tests             # needs Docker
dotnet test tests/Baton.Integration.Tests --filter Category=Load   # throughput smoke, by hand
dotnet run --project src/Baton.ControlPlane.Api       # http://localhost:5100
dotnet run --project src/Baton.Orchestrator           # http://localhost:5200 (gRPC + /healthz)
dotnet run --project src/Baton.Agent
dotnet run --project src/Baton.Web                    # http://localhost:5300

# EF Core (from repo root; `dotnet tool restore` once for the pinned dotnet-ef)
dotnet ef migrations add <Name> -p src/Baton.Persistence -s src/Baton.ControlPlane.Api
dotnet ef database update      -p src/Baton.Persistence -s src/Baton.ControlPlane.Api

# OpenSpec (CLI pinned in CI)
npx @fission-ai/openspec@1.14.1 list                  # changes in flight + task progress
npx @fission-ai/openspec@1.14.1 show <change-id>
npx @fission-ai/openspec@1.14.1 validate --all --strict
npx @fission-ai/openspec@1.14.1 archive <change-id>   # when the milestone ships
```

## How to work

A milestone moves through five gates; the owner opens the first and closes the last.

1. **Discuss before implementing.** Read the milestone doc and its change (`proposal.md`, `design.md`,
   delta specs, `tasks.md`), then bring the owner the open decisions: gaps in the spec, open questions,
   defaults that end up in hashes or the public SDK. Record each answer in the change's `design.md` (and
   the delta spec when behavior changes). Code starts once the owner agrees.
2. **Implement on a branch** `m<N>/<change-slug>`, task by task (`.agents/skills/openspec-apply-change`).
   Each task says how it is verified; tick `- [x]` only when that verification passes.
   - Commit per task or per group of related tasks, the ticks in the same commit ("Commits" below).
   - Behavior not covered by the change → stop and propose one (`.agents/skills/openspec-propose`).
     Small fixes that don't change specified behavior need no change.
   - Architectural decision (new dependency, new infrastructure, changed invariant below, settling an
     open question) → ADR in `docs/adr/` with status *Proposed*. Never silently deviate from an accepted
     ADR.
3. **Open the PR** when every task is ticked: linked to the GitHub milestone, every exit criterion ticked
   except the archive. The change stays in `openspec/changes/` and the GitHub milestone stays open.
4. **Owner review.** Address review in further commits; wait for the owner's explicit OK.
5. **Close on the owner's OK**, in this order:
   - Last commit on the PR branch: `openspec archive <change-id>`, tick the archive exit criterion,
     mark the milestone done in `ROADMAP.md` and `README.md`.
   - Close the GitHub milestone (`docs/milestones/README.md` "GitHub milestones").
   - Merge the PR once CI is green.

Traceability: requirement IDs (`AUT-xx`, `WF-xx`, `ORC-xx`, `HST-xx`, `OBS-xx`, `NFR-*`) appear in spec
headers, tasks, test names where useful, and PR descriptions.

## Architecture invariants — do not break

- **Postgres only** (ADR-0003). No message broker, Redis, or second datastore without a new ADR.
- **One transaction per state transition.** Claim, complete + DAG advance + limit release, reap, admit —
  each is a single Postgres transaction. No "update then publish" sequences.
- **Time comes from Postgres `now()`**, never from agent or orchestrator clocks.
- **Fencing** (ADR-0006). Every agent → orchestrator write after a claim carries the lease token and is
  rejected (`FAILED_PRECONDITION`) if it doesn't match. Never bypass the token check.
- **At-least-once.** Handlers must be idempotent; use `TaskContext.IdempotencyKey` (`runId:taskKey`).
- **Definitions are immutable** (ADR-0004). Any change to a workflow's graph or task options requires
  bumping `Version`. Never edit a registered `key@version`.
- **Hot-path SQL is hand-written** in `Baton.Persistence/Queue` (ADR-0007). Don't route claim/heartbeat/
  complete/reaper through EF Core.
- **Locks.** `SKIP LOCKED` for row-oriented loops; `pg_try_advisory_xact_lock` (transaction-scoped only) for
  singleton loops; lock `concurrency_limits` rows in key order (ADR-0008, ADR-0009).
- **Agents pull, nothing pushes** (ADR-0005). Agents need outbound access only.
- **No secrets in parameters, logs or the repo.** Parameters reference secrets by name.
- **Web talks to the API only** — never to the database directly.

## Coding conventions

- File-scoped namespaces; nullable enabled; `async` all the way with `CancellationToken` passed through.
- Prefer primary constructors and `sealed` classes; records for messages/DTOs.
- IDs: `Guid.CreateVersion7()` (ADR-0010). Fencing tokens: `lease_token_seq`.
- Package versions only in `Directory.Packages.props` (central package management). One bump per PR.
- `Baton.Domain` stays pure: no EF, Npgsql, ASP.NET or I/O references.
- Public SDK (`Baton.Abstractions`) changes are breaking for workflow authors — call them out in the PR.
- Logging: structured templates (`"Claimed {Count} tasks for {Host}"`) through `[LoggerMessage]`
  source-generated methods (CA1848 is a build error).

## Commits

[Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/): `<type>(<scope>): <summary>`,
summary in the imperative, lowercase, no trailing period, at most 72 characters.

- Types: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `build` (packages, SDK, solution), `ci`,
  `chore` (everything else, including OpenSpec bookkeeping).
- Scope: the area touched — `sdk`, `domain`, `persistence`, `api`, `orchestrator`, `agent`, `web`,
  `protocol`, `definitions`, `openspec`, `deps`. Omit it when the commit spans several areas.
- Body: what and why. Footer: the milestone tasks and requirements, e.g. `Refs: M2 2.1, 2.2; HST-01`.
- Breaking changes (public SDK, `agent.proto`, REST contract): `!` after the scope and a
  `BREAKING CHANGE:` footer.
- PR titles follow the same format. The closing commit of a milestone is
  `chore(openspec): archive <change-id>`.

```text
feat(domain): validate workflow graphs at startup

Kahn cycle detection, root presence, key rules and option bounds.

Refs: M1 2.2; WF-03
```

## Testing rules

- Every spec scenario has at least one test that would fail without the behavior.
- Concurrency-sensitive SQL (claims, fencing, admission, limits) needs an **integration test with
  concurrent actors** against Testcontainers Postgres — a unit test with mocks is not enough.
- Domain logic (DAG, trigger rules, backoff, aging) is unit-tested with injected randomness/time.
- Test names describe behavior: `Stale_token_complete_is_rejected_without_state_change`.

## Definition of done (per PR)

- [ ] Linked to a milestone; OpenSpec change tasks ticked (or "no behavior change" stated)
- [ ] `dotnet build` clean, `dotnet test` green, `openspec validate --all --strict` green
- [ ] New/changed behavior covered by tests; concurrency paths by integration tests
- [ ] ADR added/updated if an architectural decision was made
- [ ] No secrets, real hostnames or connection strings committed

## Don'ts

- Don't add packages, services or infrastructure that an ADR doesn't cover.
- Don't change `agent.proto` field numbers or semantics in place — add fields, or create `v2`.
- Don't edit `docs/design/design-spec.md` to "fix" it; record changes in OpenSpec/ADRs instead.
- Don't mark OpenSpec tasks done without running their verification.
- Don't hand-edit `openspec/specs/` for in-flight work — it changes only via archive.
