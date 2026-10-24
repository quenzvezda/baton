# Baton — Workflow Engine & Automations Platform Design Spec

Oct 22, 2026 · @Ripan

## 1. Overview

Baton lets users set up automations in a web UI; each automation triggers a workflow (a DAG of C# tasks, Airflow-style), and an orchestrator routes that workflow's tasks to a group of worker hosts under limits, priorities and leases. PostgreSQL is the single source of truth for configuration, run state, the task queue and leases.

**Stack:** .NET 10, ASP.NET Core, gRPC, EF Core + Npgsql, PostgreSQL 16+.

**In scope for v1:** workflows defined statically in C#, cron / manual / webhook triggers, host groups, concurrency limits, priority with aging, leases with fencing, retries, cancellation, run monitoring.

**Out of scope for v1:** authoring workflows at runtime (a JSON/YAML format is planned for phase 3), preemption of running tasks, multi-region. Existing engines (Temporal's .NET SDK, Hangfire, Elsa) cover parts of this; building gives DAG workflows with host-group routing on Postgres alone.

| Term | Meaning |
| --- | --- |
| Workflow definition | A versioned DAG of tasks, written as a C# class and identified by `key@version`. |
| Task | One node in the DAG, bound to a C# handler class, with its own retries, timeout and trigger rule. |
| Automation | A user-configured binding of triggers, a workflow definition, parameters, a target host group, priority and limits. |
| Trigger | What starts an automation: a cron schedule, a manual "Run now", or a signed webhook. |
| Workflow run | One execution of a definition, created when a trigger fires. |
| Task run / attempt | One task inside a run; each try at executing it is an attempt. |
| Host (agent) | A .NET worker process that claims tasks and runs their handlers. |
| Host group | A named pool of hosts; the routing target of an automation, with its own limits. |
| Lease | A time-limited, renewable claim a host holds on a task run, protected by a fencing token. |
| Limit | A cap on concurrent runs or tasks, per automation, workflow, host group or host. |
| Priority | An integer from 0 to 100, higher first, that ages upward while work waits. |

## 2. Functional requirements

49 requirements across five areas; 36 are Must for v1. Priorities are proposals — change them in the Priority column.

### 2.1 Automations

| ID | Requirement | Priority |
| --- | --- | --- |
| AUT-01 | Users can create, edit, clone, enable, disable and delete an automation with a name, description and owner. | Must |
| AUT-02 | An automation selects one workflow definition and either pins a version or follows the latest. | Must |
| AUT-03 | Users enter workflow parameters in a form generated from the definition's parameter schema; invalid values are rejected on save. | Must |
| AUT-04 | An automation has one or more triggers: cron (with an IANA time zone), manual "Run now", or an HMAC-signed webhook. | Must |
| AUT-05 | An automation names a target host group and, optionally, host labels every task requires. | Must |
| AUT-06 | An automation sets a priority (0–100) and a maximum number of concurrent runs. | Must |
| AUT-07 | When a trigger fires while runs are still active, the concurrency policy decides: Allow, Skip, Queue, or Replace (cancel the oldest). | Must |
| AUT-08 | After downtime, the cron misfire policy decides: FireOnce, Skip, or CatchUp (up to N missed fires). | Should |
| AUT-09 | An automation sets a run timeout; a run exceeding it is cancelled and marked TimedOut. | Must |
| AUT-10 | The editor previews the next 5 fire times of each cron trigger. | Should |
| AUT-11 | Every change to an automation is written to an audit log (who, when, before and after). | Should |
| AUT-12 | "Run now" can override parameters and priority for that one run. | Could |

### 2.2 Workflow engine

| ID | Requirement | Priority |
| --- | --- | --- |
| WF-01 | Workflow definitions are C# classes in a shared Definitions assembly; there is no runtime authoring in v1. | Must |
| WF-02 | A definition is a DAG of tasks with explicit dependencies; independent branches run in parallel. | Must |
| WF-03 | Definitions are validated at startup (acyclic, unique task keys, handlers resolvable, parameters serializable); an invalid definition stops startup. | Must |
| WF-04 | Definitions are versioned and immutable: the same `key@version` with a different graph hash is rejected, and every run records the exact version it used. | Must |
| WF-05 | Each task can set retries and backoff, a timeout, a trigger rule, required host labels and a priority offset. | Must |
| WF-06 | Trigger rules: AllSuccess (default), AllDone, OneFailed. | Must |
| WF-07 | A task receives the workflow parameters and its upstream tasks' outputs, and returns a JSON output of up to 256 KB. | Must |
| WF-08 | A handler can throw `NonRetryableException` to fail at once without using its retries. | Must |
| WF-09 | The catalog page shows each definition's DAG, parameter schema and version history. | Must |
| WF-10 | A failed run can be re-run from its failed tasks, reusing the outputs of tasks that succeeded. | Should |
| WF-11 | Definitions can be written as JSON/YAML that reference registered handlers, and edited in the UI. | Could |

### 2.3 Orchestration

| ID | Requirement | Priority |
| --- | --- | --- |
| ORC-01 | Each trigger firing creates exactly one workflow run (idempotency key = automation + trigger + fire time). | Must |
| ORC-02 | A new run stays Pending until admission confirms every applicable limit has room (automation, workflow, host group). | Must |
| ORC-03 | A task becomes Queued when all its upstream tasks have finished and its trigger rule holds; otherwise it is Skipped. | Must |
| ORC-04 | A queued task goes only to an eligible host: online, in the target group, carrying the required labels, running a build that contains the definition version, with a free slot. | Must |
| ORC-05 | Within a group, dispatch order is effective priority (highest first), then queue time (oldest first). | Must |
| ORC-06 | Effective priority ages upward while a task waits (default +1 per 5 minutes, capped at +20). | Should |
| ORC-07 | A dispatched task is held under a lease (default 60 s) that the host renews by heartbeat; each lease carries a fencing token. | Must |
| ORC-08 | Results from a host whose lease has expired or been replaced are rejected. | Must |
| ORC-09 | When a lease expires, the attempt is recorded as Lost and the task is retried by its policy. | Must |
| ORC-10 | Retries wait with exponential backoff and jitter. | Must |
| ORC-11 | Users can cancel a run: queued tasks are cancelled and running tasks get a cancellation signal with a grace period. | Must |
| ORC-12 | Placement mode per automation: PerTask (any host in the group for each task) or PinnedRun (every task on the host that ran the first one). | Should |
| ORC-13 | Operators can pause and resume dispatch for a host group. | Should |
| ORC-14 | Named resource pools (for example "erp-api: 4 slots") cap concurrent tasks across groups. | Could |

### 2.4 Hosts and host groups

| ID | Requirement | Priority |
| --- | --- | --- |
| HST-01 | Admins create, edit and delete host groups and their limits: max concurrent runs and max concurrent tasks. | Must |
| HST-02 | An agent registers itself on start with its name, group, labels, slot capacity, agent version and supported definition versions. | Must |
| HST-03 | Agents heartbeat every 10 s; a host with no heartbeat for 30 s is marked Offline. | Must |
| HST-04 | Operators can drain a host: it finishes its running tasks and takes no new ones. | Must |
| HST-05 | Each host belongs to exactly one group; a move takes effect on the host's next claim. | Must |
| HST-06 | The hosts page shows status, slots used and total, current tasks and last heartbeat. | Must |

### 2.5 Monitoring and UI

| ID | Requirement | Priority |
| --- | --- | --- |
| OBS-01 | The runs list filters by automation, workflow, status, host group and date range. | Must |
| OBS-02 | Run detail shows the DAG coloured by task state, with attempts, hosts, timings, outputs and errors per task. | Must |
| OBS-03 | Logs a handler writes through `ILogger` are captured and viewable live and afterwards. | Must |
| OBS-04 | A queue dashboard shows queued tasks and oldest wait per group, active leases, and lease expirations per hour. | Should |
| OBS-05 | Run failure or timeout sends a notification by email or webhook, set per automation. | Should |
| OBS-06 | Traces, metrics and logs are exported through OpenTelemetry. | Should |

## 3. Non-functional requirements

Tasks run at least once, so handlers must be idempotent; everything else follows from keeping every state change inside a Postgres transaction. The targets below are proposals — confirm them against expected load (see Open questions).

| Attribute | Proposed target | How the design meets it |
| --- | --- | --- |
| Execution guarantee | At-least-once per task; no task runs twice at the same time | Leases with fencing tokens; handlers get an idempotency key (`runId:taskKey`) |
| Durability | No accepted run is lost when any process crashes | Every transition is a Postgres transaction; agents hold no state the orchestrator needs |
| Dispatch latency | p95 under 2 s from Queued to Leased when a slot is free | Long-poll claims woken by `LISTEN/NOTIFY`; partial index on queued tasks |
| Trigger accuracy | Cron runs created within 5 s of their fire time | 1 s trigger loop over an index on `next_fire_at` |
| Throughput | 100k task runs per day, peaks of 50 dispatches per second | `FOR UPDATE SKIP LOCKED` queue; claims batch several tasks per call |
| Availability | 99.9% for API and orchestrator | 2+ replicas of each; singleton loops elected with Postgres advisory locks; Postgres primary + streaming replica |
| Scale-out | New hosts join without orchestrator changes | Agents self-register into their group |
| Rolling deploys | Agent upgrades fail no runs | Drain mode; dispatch matches tasks to hosts that carry their definition version |
| Security | SSO for people, strong identity for agents, no secrets in parameters | OIDC login with Viewer / Operator / Admin roles; agents use mTLS or per-host tokens; parameters reference secrets by name |
| Auditability | Every config change and run transition is traceable | Audit log on automations and groups; `run_events` table per run |
| Retention | Run history kept 90 days | Monthly partitions on history tables; old partitions dropped |

## 4. Architecture

Four deployables share one Postgres database: the web UI, the control-plane API, the orchestrator, and any number of agents organised into host groups. Agents pull work; nothing pushes to them, so a host behind a firewall only needs outbound access to the orchestrator.

&#91;embedded content: system architecture · 4 deployables, 1 database\]

The etl and gpu groups are examples. The UI and API can ship as one process in v1, and so can the API and orchestrator if the team prefers fewer deployables.

### Components

| Component | Built with | Responsibility | Scaling |
| --- | --- | --- | --- |
| Web UI | Blazor Server (React is the alternative) | Automation editor, workflow catalog, runs, hosts, queue dashboard | Stateless, behind the API |
| Control Plane API | ASP.NET Core minimal APIs, EF Core | CRUD for automations, triggers and host groups; Run now; webhook intake; registers definitions at startup | Stateless, N replicas |
| Orchestrator | .NET `BackgroundService`s + a gRPC service | Trigger loop, admission, DAG advance, dispatch, lease reaping, retry promotion, priority aging | N replicas; singleton loops elected with `pg_try_advisory_xact_lock` |
| Agent | .NET Worker Service | Registers, heartbeats, claims tasks, runs handlers with timeouts, streams logs, reports results | One process per host; add hosts freely |
| Definitions assembly | .NET class library, shipped as a NuGet package | Workflow classes and task handlers; referenced by API, orchestrator and agents | Versioned with each release |
| PostgreSQL | 16+ | All state, the task queue, leases, wake-ups through `LISTEN/NOTIFY` | Primary + streaming replica; history tables partitioned by month |

### How a run flows

1. **Trigger.** The trigger loop finds cron triggers with `next_fire_at <= now()` (or receives a webhook or Run now), applies the automation's concurrency policy, and inserts a Pending workflow run with a unique idempotency key.
2. **Admission.** Admission takes Pending runs in priority order, reserves every applicable limit in one transaction, marks the run Running, and creates its task runs: roots Queued, the rest Waiting.
3. **Claim.** An agent long-polls `Claim` with its free slot count. The dispatcher picks the best eligible queued tasks with `SKIP LOCKED`, marks them Leased with a fresh fencing token and a 60 s expiry, and returns them.
4. **Execute.** The agent runs the handler, renews all its leases in one heartbeat every 10 s, and streams logs.
5. **Complete.** The agent reports the result with the fencing token. In one transaction the orchestrator records it, decrements downstream counters, and queues (or skips) the tasks whose upstream is done, then sends `NOTIFY` to wake waiting claims.
6. **Finish.** When every task is final, the run gets its final status, its limits are released, and notifications fire.
7. **Recover.** The reaper moves tasks with expired leases to Retry wait or Failed, promotes due retries to Queued, and cancels runs past their deadline.

## 5. Workflow definition model

A workflow is a C# class that declares its tasks and edges through a fluent builder; at startup the graph is validated, hashed and registered in Postgres, so the UI and orchestrator read definitions from the database while handler code ships with the agents.

### Abstractions

```csharp
// Baton.Abstractions
public interface IWorkflowDefinition
{
    string Key { get; }          // "nightly-sales-report"
    int Version { get; }         // bump on any change to the graph or options
    Type ParamsType { get; }
    void Build(IWorkflowBuilder builder);
}

public abstract class WorkflowDefinition<TParams> : IWorkflowDefinition
{
    public abstract string Key { get; }
    public abstract int Version { get; }
    public Type ParamsType => typeof(TParams);
    public void Build(IWorkflowBuilder builder) => Define(builder.For<TParams>());
    protected abstract void Define(IWorkflowBuilder<TParams> wf);
}

public interface ITaskHandler<TParams, TOutput>
{
    Task<TOutput> ExecuteAsync(TaskContext<TParams> ctx, CancellationToken ct);
}

public sealed class TaskContext<TParams>
{
    public required Guid RunId { get; init; }
    public required string TaskKey { get; init; }
    public required int Attempt { get; init; }
    public required TParams Params { get; init; }
    public required ILogger Logger { get; init; }       // captured into task logs
    public string IdempotencyKey => $"{RunId}:{TaskKey}"; // stable across retries
    public T GetOutput<T>(string upstreamTaskKey) => /* throws if not an ancestor */ default!;
}

public enum TriggerRule { AllSuccess, AllDone, OneFailed }
public sealed class NonRetryableException(string message) : Exception(message);
```

### Example definition

Two extracts run in parallel, the report waits for both, and a notification runs only if something failed.

```csharp
public sealed record SalesReportParams(string Region, DateOnly? ReportDate, string[] Recipients);

public sealed class NightlySalesReport : WorkflowDefinition<SalesReportParams>
{
    public override string Key => "nightly-sales-report";
    public override int Version => 2;

    protected override void Define(IWorkflowBuilder<SalesReportParams> wf)
    {
        var orders = wf.Task<ExtractOrders>("extract-orders")
            .Retry(maxAttempts: 4, initialDelay: TimeSpan.FromSeconds(30))
            .Timeout(TimeSpan.FromMinutes(15));

        var customers = wf.Task<ExtractCustomers>("extract-customers")
            .Retry(3, TimeSpan.FromSeconds(30));

        var report = wf.Task<BuildReport>("build-report")
            .DependsOn(orders, customers)
            .RequireLabels("large-memory");

        wf.Task<PublishReport>("publish-report").DependsOn(report);

        wf.Task<NotifyOnFailure>("notify-failure")
            .DependsOn(orders, customers, report)
            .When(TriggerRule.OneFailed);
    }
}

public sealed class BuildReport(IReportRenderer renderer)
    : ITaskHandler<SalesReportParams, ReportOutput>
{
    public async Task<ReportOutput> ExecuteAsync(
        TaskContext<SalesReportParams> ctx, CancellationToken ct)
    {
        var orders = ctx.GetOutput<OrdersExtract>("extract-orders");
        var customers = ctx.GetOutput<CustomersExtract>("extract-customers");
        ctx.Logger.LogInformation("Rendering {Count} orders for {Region}",
            orders.Count, ctx.Params.Region);

        var uri = await renderer.RenderAsync(orders, customers, ctx.IdempotencyKey, ct);
        return new ReportOutput(uri);
    }
}
```

Handlers are resolved from DI per attempt, so they can take constructor dependencies. One line registers everything in an assembly: `services.AddWorkflows(typeof(NightlySalesReport).Assembly);`

### Startup validation

The API refuses to start if any check fails, so a broken definition never reaches the catalog.

- Task keys are unique within the workflow, kebab-case, at most 64 characters.
- The graph is acyclic (Kahn's topological sort) and has at least one root.
- Every handler is registered in DI and its `TParams` matches the workflow's.
- The parameter type serialises with `System.Text.Json` and yields a JSON Schema (`JsonSchemaExporter`), which the UI renders as the automation's parameter form.
- Retry and timeout values fall within platform bounds (proposed: at most 10 attempts, at most 24 h per task).

### Registration and versioning

- The builder output is serialised to canonical JSON (tasks sorted by key, options included) and hashed with SHA-256.
- The API upserts `(key, version, hash, graph, params_schema)` into `workflow_definitions`. The same `key@version` with a different hash is a startup error: change the graph, bump the version.
- Old versions stay in the catalog; runs and pinned automations keep pointing at them until they are retired.
- Each agent reports the definition hashes its build contains, and the dispatcher sends a task only to agents carrying that hash. During a rolling deploy, old runs finish on old agents and new runs go to new ones.

### Data between tasks

- A task's return value is stored as JSON in `task_runs.output`, capped at 256 KB.
- Larger data goes to blob storage; the task returns a reference (a URI) instead.
- `GetOutput<T>` on a task that is not an ancestor throws at run time, because that dependency would not be guaranteed to have run.

### Trigger rules

| Rule | The task runs when all upstream tasks are final and | Otherwise |
| --- | --- | --- |
| AllSuccess (default) | every upstream task Succeeded | Skipped |
| AllDone | always | — |
| OneFailed | at least one upstream task Failed | Skipped |

Skipped counts as "not succeeded", so a Skipped task skips its AllSuccess children too. A run ends Failed if any task ended Failed; Skipped tasks alone never fail a run.

## 6. Orchestration

The orchestrator decides when a run may start (admission, against limits), which tasks are ready (the DAG advancer), and which host gets each task (the dispatcher, by priority and eligibility), then keeps that assignment honest with leases. Each host group is an independent queue.

### Task states

&#91;embedded content: task run states · 9 states, 4 final\]

A workflow run moves Pending → Running → Succeeded, Failed, Cancelled or TimedOut, with Cancelling in between when a user cancels.

### Routing and eligibility

Every task of a run goes to the automation's host group; tasks can narrow the hosts further with labels. A host may take a queued task only if all of these hold:

- the host is Online (not Draining or Offline) and the group's dispatch is not paused;
- its labels include the automation's and the task's required labels;
- its build carries the task's definition hash;
- it has a free slot, and the group is under its max concurrent tasks;
- for PinnedRun automations, the run is not yet pinned or is pinned to this host.

With PinnedRun, the first claim pins the run to that host and every later task waits for it. If the pinned host goes Offline, the run fails with "pinned host lost" (see Open questions for re-pinning).

### Limits

Run-level limits are reserved at admission; task-level limits at claim time. Each reservation is a row in `concurrency_limits`, updated in the same transaction as the state change it guards, and released when the run or task finishes.

| Limit | Key in `concurrency_limits` | Checked at | Default |
| --- | --- | --- | --- |
| Automation max concurrent runs | `automation:{id}` | Trigger (policy) and admission | 1 |
| Workflow max concurrent runs | `workflow:{key}` | Admission | Unlimited |
| Host group max concurrent runs | `group-runs:{id}` | Admission | Unlimited |
| Host group max concurrent tasks | `group-tasks:{id}` | Claim | Unlimited |
| Host slots | none — the agent reports free slots | Claim | Set per host |
| Resource pool (phase 3) | `pool:{name}` | Claim | — |

Admission reads up to 100 Pending runs per group in priority order, locks each run's limit rows in key order (so two replicas never deadlock), and starts the run only if every row has room. A run that does not fit is passed over, so it never blocks lower-priority runs that do fit.

The automation's concurrency policy applies when a trigger fires while the automation is at its limit:

| Policy | What happens |
| --- | --- |
| Skip (default) | No run is created; the skipped fire is logged in `run_events`. |
| Queue | The run is created and waits in Pending until a slot frees. |
| Replace | The oldest active run is cancelled, then the new run is created. |
| Allow | The automation limit is ignored; workflow and group limits still apply. |

### Priority and aging

A task's base priority is its run's priority (the automation's, or a Run now override) plus the task's offset, clamped to 0–100. While it waits, it gains one point per aging step up to a cap, so low-priority work cannot starve:

```latex
p_{\text{effective}} = \min\left(100,\; p_{\text{base}} + \min\left(\text{cap},\; \left\lfloor \frac{t_{\text{wait}}}{\text{step}} \right\rfloor\right)\right)
```

Defaults: step 5 minutes, cap 20. An aging loop recomputes `effective_priority` every 30 s, touching only rows whose value changes, so the dispatch index stays usable. Priority orders work within a group; groups never compete with each other.

### Leases and fencing

- A claim sets `lease_token = nextval('lease_token_seq')` and `lease_expires_at = now() + 60 s`. All times come from Postgres `now()`, never from agent clocks.
- One heartbeat every 10 s renews every lease the host holds. The reply lists tasks the host no longer owns and tasks to cancel; the agent cancels those handlers.
- Every Complete call carries the token. A token that no longer matches gets `FAILED_PRECONDITION`, and the agent discards that result.
- The reaper runs every 5 s. An expired lease ends the attempt as Lost; the task goes to Retry wait if attempts remain, otherwise Failed.
- 60 s against a 10 s heartbeat tolerates five missed beats (GC pauses, network blips) and still recovers a dead host's work in about a minute. Both values are tunable per group.

### Retries and timeouts

```latex
\text{delay}_n = \min\left(\text{max},\; \text{initial} \times \text{multiplier}^{\,n-1}\right) \times (1 + j), \quad j \in [0,\, 0.2]
```

- Defaults: 3 attempts, initial 30 s, multiplier 2, max 10 minutes. `NonRetryableException` skips straight to Failed.
- The agent enforces the task timeout with `CancelAfter`; the reaper fails any task running past its timeout plus 60 s as a backstop. A timeout is a retryable error.
- A run's `deadline_at` = start + run timeout; past it, the reaper cancels the run and marks it TimedOut.

### Cancellation

1. The run moves to Cancelling.
2. Its Waiting, Queued and Retry wait tasks become Cancelled at once.
3. Leased and Running tasks get `cancel_requested = true`; the next heartbeat reply tells the agent, which cancels the handler's token.
4. The agent reports Cancelled. If it has not within 30 s, lease expiry closes the task as Cancelled, never retried.
5. When every task is final, the run becomes Cancelled and its limits are released.

### High availability

- Every loop is safe on several replicas: claims, the reaper and the trigger loop use `SKIP LOCKED`; admission and aging take an advisory lock per tick.
- A replica crash rolls back its open transaction; another replica picks the work up on its next tick.
- Running tasks survive orchestrator restarts: agents keep working and send their next heartbeat to any replica behind the load balancer.

## 7. Postgres data model

Eleven tables hold everything; `task_runs` is both the DAG state and the work queue, so claiming a task, advancing the DAG and releasing limits happen in one transaction with no separate broker.

| Table | Holds | Grows with |
| --- | --- | --- |
| `workflow_definitions` | Registered `key@version` graphs, hashes, parameter schemas | Releases |
| `host_groups` | Groups, their limits, the dispatch pause flag | Config |
| `hosts` | Registered agents, labels, slots, supported hashes, last heartbeat | Hosts |
| `automations` | User config: workflow, parameters, routing, priority, policies | Config |
| `automation_triggers` | Cron / webhook / manual triggers and their next fire time | Config |
| `workflow_runs` | One row per run | Runs |
| `task_runs` | One row per task per run; the queue and the leases | Runs × tasks |
| `task_attempts` | One row per attempt (partitioned monthly) | Attempts |
| `task_logs` | Log lines from handlers (partitioned monthly) | Log volume |
| `run_events` | Audit timeline of every transition (partitioned monthly) | Transitions |
| `concurrency_limits` | One counter row per limit key | Config |

### Schema

```sql
CREATE SEQUENCE lease_token_seq;

CREATE TABLE workflow_definitions (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  key           text NOT NULL,
  version       int  NOT NULL,
  graph_hash    char(64) NOT NULL,           -- SHA-256 of the canonical graph JSON
  graph         jsonb NOT NULL,              -- tasks, edges, options
  params_schema jsonb NOT NULL,              -- JSON Schema for the UI form
  display_name  text NOT NULL,
  registered_at timestamptz NOT NULL DEFAULT now(),
  is_retired    boolean NOT NULL DEFAULT false,
  UNIQUE (key, version)
);

CREATE TABLE host_groups (
  id                   uuid PRIMARY KEY,
  name                 text NOT NULL UNIQUE,
  max_concurrent_runs  int,                  -- NULL = unlimited
  max_concurrent_tasks int,
  lease_seconds        int NOT NULL DEFAULT 60,
  dispatch_paused      boolean NOT NULL DEFAULT false
);

CREATE TABLE hosts (
  id                uuid PRIMARY KEY,
  name              text NOT NULL UNIQUE,
  host_group_id     uuid NOT NULL REFERENCES host_groups(id),
  labels            text[] NOT NULL DEFAULT '{}',
  capacity_slots    int NOT NULL CHECK (capacity_slots > 0),
  status            text NOT NULL CHECK (status IN ('online','draining','offline')),
  agent_version     text NOT NULL,
  definition_hashes text[] NOT NULL,         -- definitions this build can run
  last_heartbeat_at timestamptz NOT NULL,
  registered_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE automations (
  id                  uuid PRIMARY KEY,
  name                text NOT NULL UNIQUE,
  description         text,
  enabled             boolean NOT NULL DEFAULT true,
  workflow_key        text NOT NULL,
  workflow_version    int,                   -- NULL = latest
  parameters          jsonb NOT NULL DEFAULT '{}',
  host_group_id       uuid NOT NULL REFERENCES host_groups(id),
  required_labels     text[] NOT NULL DEFAULT '{}',
  priority            smallint NOT NULL DEFAULT 50 CHECK (priority BETWEEN 0 AND 100),
  max_concurrent_runs int NOT NULL DEFAULT 1,
  concurrency_policy  text NOT NULL DEFAULT 'skip'
                      CHECK (concurrency_policy IN ('skip','queue','replace','allow')),
  placement_mode      text NOT NULL DEFAULT 'per_task'
                      CHECK (placement_mode IN ('per_task','pinned_run')),
  run_timeout         interval,
  owner               text NOT NULL,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  row_version         int NOT NULL DEFAULT 0  -- optimistic concurrency for the editor
);

CREATE TABLE automation_triggers (
  id                  uuid PRIMARY KEY,
  automation_id       uuid NOT NULL REFERENCES automations(id) ON DELETE CASCADE,
  kind                text NOT NULL CHECK (kind IN ('cron','webhook','manual')),
  cron_expr           text,
  time_zone           text,                  -- IANA, e.g. 'Asia/Singapore'
  misfire_policy      text NOT NULL DEFAULT 'fire_once'
                      CHECK (misfire_policy IN ('fire_once','skip','catch_up')),
  next_fire_at        timestamptz,
  last_fired_at       timestamptz,
  webhook_secret_hash bytea,
  enabled             boolean NOT NULL DEFAULT true
);
CREATE INDEX ix_triggers_due ON automation_triggers (next_fire_at)
  WHERE enabled AND kind = 'cron';

CREATE TABLE workflow_runs (
  id              uuid PRIMARY KEY,          -- UUIDv7 (Guid.CreateVersion7), time-ordered
  automation_id   uuid REFERENCES automations(id),
  definition_id   bigint NOT NULL REFERENCES workflow_definitions(id),
  status          text NOT NULL CHECK (status IN ('pending','running','cancelling',
                  'succeeded','failed','cancelled','timed_out')),
  priority        smallint NOT NULL,
  host_group_id   uuid NOT NULL REFERENCES host_groups(id),
  pinned_host_id  uuid REFERENCES hosts(id),
  parameters      jsonb NOT NULL,
  trigger_kind    text NOT NULL,
  idempotency_key text NOT NULL UNIQUE,      -- automation:trigger:fire-time
  held_limits     text[] NOT NULL DEFAULT '{}', -- limit keys to release at the end
  created_at      timestamptz NOT NULL DEFAULT now(),
  started_at      timestamptz,
  finished_at     timestamptz,
  deadline_at     timestamptz
);
CREATE INDEX ix_runs_admission ON workflow_runs (host_group_id, priority DESC, created_at)
  WHERE status = 'pending';
CREATE INDEX ix_runs_deadline ON workflow_runs (deadline_at)
  WHERE status IN ('running','cancelling');
CREATE INDEX ix_runs_by_automation ON workflow_runs (automation_id, created_at DESC);

CREATE TABLE task_runs (
  id                    uuid PRIMARY KEY,
  workflow_run_id       uuid NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
  task_key              text NOT NULL,
  status                text NOT NULL CHECK (status IN ('waiting','queued','leased',
                        'running','retry_wait','succeeded','failed','skipped','cancelled')),
  trigger_rule          text NOT NULL,
  upstream_remaining    int NOT NULL,        -- decremented as upstream tasks finish
  upstream_failed       int NOT NULL DEFAULT 0,
  upstream_unsuccessful int NOT NULL DEFAULT 0, -- failed + skipped + cancelled
  attempt               int NOT NULL DEFAULT 0,
  max_attempts          int NOT NULL,
  timeout               interval NOT NULL,
  priority              smallint NOT NULL,
  effective_priority    smallint NOT NULL,
  host_group_id         uuid NOT NULL,
  required_labels       text[] NOT NULL DEFAULT '{}',
  definition_hash       char(64) NOT NULL,
  pinned_host_id        uuid,                -- copied from the run under PinnedRun
  queued_at             timestamptz,
  not_before            timestamptz,         -- when a retry becomes due
  lease_host_id         uuid,
  lease_token           bigint,              -- fencing token, new on every claim
  lease_expires_at      timestamptz,
  cancel_requested      boolean NOT NULL DEFAULT false,
  output                jsonb,
  error                 jsonb,
  started_at            timestamptz,
  finished_at           timestamptz,
  UNIQUE (workflow_run_id, task_key)
) WITH (fillfactor = 80);                     -- room for HOT updates

CREATE INDEX ix_tasks_dispatch ON task_runs (host_group_id, effective_priority DESC, queued_at)
  WHERE status = 'queued';
CREATE INDEX ix_tasks_leases ON task_runs (lease_expires_at)
  WHERE status IN ('leased','running');
CREATE INDEX ix_tasks_retry_due ON task_runs (not_before)
  WHERE status = 'retry_wait';
CREATE INDEX ix_tasks_by_host ON task_runs (lease_host_id)
  WHERE lease_host_id IS NOT NULL;

CREATE TABLE task_attempts (
  task_run_id uuid NOT NULL,
  attempt     int NOT NULL,
  host_id     uuid NOT NULL,
  lease_token bigint NOT NULL,
  started_at  timestamptz NOT NULL,
  finished_at timestamptz,
  outcome     text,                          -- succeeded | failed | timed_out | lost | cancelled
  error       jsonb,
  PRIMARY KEY (task_run_id, attempt, started_at)
) PARTITION BY RANGE (started_at);

CREATE TABLE task_logs (
  task_run_id uuid NOT NULL,
  attempt     int NOT NULL,
  seq         int NOT NULL,
  logged_at   timestamptz NOT NULL,
  level       smallint NOT NULL,
  message     text NOT NULL,
  PRIMARY KEY (task_run_id, attempt, seq, logged_at)
) PARTITION BY RANGE (logged_at);

CREATE TABLE run_events (
  id              bigint GENERATED ALWAYS AS IDENTITY,
  workflow_run_id uuid NOT NULL,
  task_key        text,
  type            text NOT NULL,             -- e.g. run.admitted, task.leased, task.lost
  data            jsonb NOT NULL DEFAULT '{}',
  at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (id, at)
) PARTITION BY RANGE (at);

CREATE TABLE concurrency_limits (
  key    text PRIMARY KEY,                    -- automation:{id}, workflow:{key}, group-runs:{id}, group-tasks:{id}
  max    int NOT NULL,
  in_use int NOT NULL DEFAULT 0 CHECK (in_use >= 0)
);
```

### Hot-path queries

**Claim** — one transaction per agent call; returns up to the agent's free slots.

```sql
-- 1. Reserve the group's task budget (only when the group has max_concurrent_tasks).
--    This row lock serialises claims within one group for a few milliseconds.
SELECT max - in_use AS room FROM concurrency_limits
WHERE key = 'group-tasks:' || @group_id FOR UPDATE;
-- budget = LEAST(@free_slots, room)

-- 2. Take the best eligible tasks without waiting on rows other writers hold.
WITH picked AS (
  SELECT id FROM task_runs
  WHERE status = 'queued'
    AND host_group_id = @group_id
    AND required_labels <@ @host_labels
    AND definition_hash = ANY (@host_definition_hashes)
    AND (pinned_host_id IS NULL OR pinned_host_id = @host_id)
  ORDER BY effective_priority DESC, queued_at
  LIMIT @budget
  FOR UPDATE SKIP LOCKED
)
UPDATE task_runs t
SET status           = 'leased',
    attempt          = t.attempt + 1,
    lease_host_id    = @host_id,
    lease_token      = nextval('lease_token_seq'),
    lease_expires_at = now() + make_interval(secs => @lease_seconds)
FROM picked
WHERE t.id = picked.id
RETURNING t.id, t.workflow_run_id, t.task_key, t.attempt, t.lease_token, t.timeout;

-- 3. UPDATE concurrency_limits SET in_use = in_use + @claimed WHERE key = 'group-tasks:' || @group_id;
```

**Heartbeat** — renews every lease the host still owns; anything missing from the result is no longer the host's.

```sql
UPDATE task_runs t
SET lease_expires_at = now() + make_interval(secs => @lease_seconds)
FROM unnest(@task_ids::uuid[], @tokens::bigint[]) AS l(id, token)
WHERE t.id = l.id AND t.lease_token = l.token
  AND t.status IN ('leased','running')
RETURNING t.id, t.cancel_requested;
```

**Complete** — fenced by the token, then the DAG advances in the same transaction.

```sql
UPDATE task_runs
SET status = @final_status, output = @output, error = @error, finished_at = now(),
    lease_host_id = NULL, lease_expires_at = NULL
WHERE id = @task_id AND lease_token = @token AND status IN ('leased','running')
RETURNING workflow_run_id, task_key;
-- 0 rows: stale result -> FAILED_PRECONDITION; the agent discards it.

-- Downstream keys come from the cached definition graph.
UPDATE task_runs
SET upstream_remaining    = upstream_remaining - 1,
    upstream_failed       = upstream_failed + @failed::int,
    upstream_unsuccessful = upstream_unsuccessful + @unsuccessful::int
WHERE workflow_run_id = @run_id AND task_key = ANY (@downstream_keys)
RETURNING id, task_key, trigger_rule, upstream_remaining, upstream_failed, upstream_unsuccessful;
-- Rows reaching upstream_remaining = 0: apply the trigger rule -> 'queued' (set queued_at)
-- or 'skipped' (and propagate). Release group-tasks, then NOTIFY tasks_ready, @group_id.
```

**Due cron triggers** — safe on every replica.

```sql
SELECT t.id, t.automation_id, t.cron_expr, t.time_zone, t.next_fire_at, t.misfire_policy
FROM automation_triggers t
JOIN automations a ON a.id = t.automation_id
WHERE t.kind = 'cron' AND t.enabled AND a.enabled AND t.next_fire_at <= now()
ORDER BY t.next_fire_at
LIMIT 100
FOR UPDATE OF t SKIP LOCKED;
-- Per row: INSERT INTO workflow_runs ... ON CONFLICT (idempotency_key) DO NOTHING;
--          UPDATE automation_triggers SET next_fire_at = @next_from_cronos, last_fired_at = @fire_time ...;
```

**Expired leases** — the reaper's batch.

```sql
SELECT id, workflow_run_id, attempt, max_attempts, cancel_requested
FROM task_runs
WHERE status IN ('leased','running') AND lease_expires_at < now()
LIMIT 500
FOR UPDATE SKIP LOCKED;
-- Each: close the attempt as 'lost'; cancel_requested -> 'cancelled';
--       attempt < max_attempts -> 'retry_wait' with not_before = now() + backoff; else 'failed'.
```

### Operational notes

- `LISTEN` needs a dedicated session connection; PgBouncer in transaction mode breaks it, so the orchestrator holds one direct connection for it. Notifications are hints only: claims also re-check every 2 s.
- Use transaction-scoped advisory locks (`pg_try_advisory_xact_lock`), which work through PgBouncer and release on rollback.
- `task_runs` is update-heavy: fillfactor 80 keeps updates HOT; set `autovacuum_vacuum_scale_factor = 0.01` on that table.
- A reconciler runs every 5 minutes and recomputes `concurrency_limits.in_use` from live rows, as a safety net against bugs.
- Monthly partitions for history tables are created ahead by `pg_partman` or a small job; partitions older than 90 days are dropped. Finished `workflow_runs` and their `task_runs` are purged on the same schedule.

## 8. C# implementation

One solution with eleven projects: five shared libraries, four deployables and two test projects. Hot-path queue SQL is hand-written with Npgsql (and Dapper); everything else uses EF Core.

### Solution layout

```text
Baton.sln
├─ src/
│  ├─ Baton.Abstractions/      IWorkflowDefinition, ITaskHandler, builder, TaskContext
│  ├─ Baton.Definitions/       your workflows and handlers (shipped as a NuGet package)
│  ├─ Baton.Domain/            state machines, DAG validator, trigger rules, backoff
│  ├─ Baton.Persistence/       EF Core DbContext + migrations, queue SQL (Npgsql/Dapper)
│  ├─ Baton.Agent.Protocol/    .proto contracts for the agent API
│  ├─ Baton.ControlPlane.Api/  REST API, webhook intake, definition registration
│  ├─ Baton.Orchestrator/      BackgroundServices + gRPC AgentService
│  ├─ Baton.Agent/             Worker Service that runs on each host
│  └─ Baton.Web/               Blazor Server UI
└─ tests/
   ├─ Baton.Domain.Tests/      DAG validation, trigger rules, state transitions
   └─ Baton.Integration.Tests/ Testcontainers Postgres: claim races, lease expiry, chaos
```

### Packages

| Need | Package |
| --- | --- |
| Postgres driver and EF Core provider | `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL` |
| Hand-written hot-path SQL | `Dapper` |
| Cron parsing with time zones | `Cronos` |
| Agent protocol | `Grpc.AspNetCore` (server), `Grpc.Net.Client` (agent) |
| Agent retries to the orchestrator | `Microsoft.Extensions.Http.Resilience` (Polly) |
| Traces, metrics, logs | `OpenTelemetry.Extensions.Hosting` + Npgsql and ASP.NET Core instrumentation |
| Integration tests | `Testcontainers.PostgreSql`, `xunit` |

Run IDs use `Guid.CreateVersion7()` (built into .NET 9+), so primary keys stay time-ordered and index-friendly.

### Orchestrator loops

Each loop is a `BackgroundService`. Loops that must run once per tick across replicas take a transaction-scoped advisory lock; loops built on `SKIP LOCKED` simply run everywhere.

```csharp
public abstract class SingletonLoop(NpgsqlDataSource db, ILogger logger) : BackgroundService
{
    protected abstract long LockId { get; }        // one constant per loop
    protected abstract TimeSpan Interval { get; }
    protected abstract Task TickAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(stop);
                await using var tx = await conn.BeginTransactionAsync(stop);
                var leader = await conn.ExecuteScalarAsync<bool>(
                    "SELECT pg_try_advisory_xact_lock(@id)", new { id = LockId }, tx);
                if (!leader) continue;              // another replica has this tick

                await TickAsync(conn, tx, stop);
                await tx.CommitAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Loop} tick failed; retrying next tick", GetType().Name);
            }
        }
    }
}

// Registered in Baton.Orchestrator/Program.cs
builder.Services.AddHostedService<TriggerLoop>();        // 1 s, SKIP LOCKED
builder.Services.AddHostedService<AdmissionLoop>();      // 1 s, advisory lock per group
builder.Services.AddHostedService<ReaperLoop>();         // 5 s, SKIP LOCKED
builder.Services.AddHostedService<RetryPromoterLoop>();  // 1 s, SKIP LOCKED
builder.Services.AddHostedService<AgingLoop>();          // 30 s, singleton
builder.Services.AddHostedService<LimitReconcilerLoop>();// 5 min, singleton
builder.Services.AddGrpc();
app.MapGrpcService<AgentService>();
```

### Agent

The agent claims only as many tasks as it has free slots, runs each with its own timeout, and lets one heartbeat loop keep every lease alive.

```csharp
public sealed class AgentWorker(AgentClient client, TaskExecutor executor, AgentOptions options)
    : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, RunningTask> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await client.RegisterAsync(options.ToRegistration(), stop);
        _ = HeartbeatLoopAsync(stop);

        while (!stop.IsCancellationRequested)
        {
            var free = options.CapacitySlots - _running.Count;
            if (free <= 0 || options.Draining) { await Task.Delay(500, stop); continue; }

            // Long-poll: returns as soon as work exists, or empty after 20 s.
            var leases = await client.ClaimAsync(free, TimeSpan.FromSeconds(20), stop);
            foreach (var lease in leases)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                cts.CancelAfter(lease.Timeout);
                _running[lease.TaskRunId] = new RunningTask(lease, cts);
                _ = RunAndReleaseAsync(lease, cts);
            }
        }
    }

    private async Task RunAndReleaseAsync(TaskLease lease, CancellationTokenSource cts)
    {
        try { await executor.RunAsync(lease, cts.Token); }
        finally { _running.TryRemove(lease.TaskRunId, out _); cts.Dispose(); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stop))
        {
            var reply = await client.HeartbeatAsync(_running.Values.Select(r => r.Lease.Ref), stop);
            foreach (var id in reply.StopTaskIds)        // lease lost or cancel requested
                if (_running.TryGetValue(id, out var task)) task.Cancellation.Cancel();
        }
    }
}

public sealed class TaskExecutor(IServiceProvider services, DefinitionRegistry registry, AgentClient client)
{
    public async Task RunAsync(TaskLease lease, CancellationToken ct)
    {
        var node = registry.Get(lease.DefinitionHash).Tasks[lease.TaskKey];
        await using var scope = services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService(node.HandlerType);

        TaskResult result;
        try
        {
            var ctx = node.CreateContext(lease, scope.ServiceProvider);
            result = TaskResult.Succeeded(await node.InvokeAsync(handler, ctx, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = lease.TimedOut ? TaskResult.TimedOut() : TaskResult.Cancelled();
        }
        catch (NonRetryableException ex) { result = TaskResult.Failed(ex, retryable: false); }
        catch (Exception ex)             { result = TaskResult.Failed(ex, retryable: true); }

        // Report even if our own token fired; the server's fencing check decides.
        await client.CompleteAsync(lease, result, CancellationToken.None);
    }
}
```

### Backoff

```csharp
public static TimeSpan NextDelay(RetryPolicy p, int attempt)
{
    var seconds = Math.Min(p.MaxDelay.TotalSeconds,
                           p.InitialDelay.TotalSeconds * Math.Pow(p.Multiplier, attempt - 1));
    return TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() * 0.2));
}
```

## 9. API surface

People and the UI use REST on the Control Plane API; agents use gRPC on the orchestrator. Roles: Viewer reads, Operator runs and cancels, Admin manages host groups and definitions.

### REST (Control Plane API)

| Method | Path | Purpose | Role |
| --- | --- | --- | --- |
| GET | `/api/workflows` | Definitions, latest version per key | Viewer |
| GET | `/api/workflows/{key}/versions/{version}` | Graph and parameter schema | Viewer |
| GET | `/api/automations` | List, filter by owner, workflow, enabled | Viewer |
| POST | `/api/automations` | Create (body below) | Operator |
| GET | `/api/automations/{id}` | Read, with triggers | Viewer |
| PUT | `/api/automations/{id}` | Update; `If-Match: <row_version>` | Operator |
| DELETE | `/api/automations/{id}` | Delete; active runs continue | Operator |
| POST | `/api/automations/{id}/enable` · `/disable` | Toggle | Operator |
| POST | `/api/automations/{id}/runs` | Run now, optional parameter and priority override | Operator |
| GET | `/api/automations/{id}/triggers/{triggerId}/preview?count=5` | Next fire times | Viewer |
| POST | `/hooks/{automationId}/{triggerId}` | Webhook trigger; `X-Signature: sha256=<HMAC>` | Signed |
| GET | `/api/runs?automation=&status=&group=&from=&to=&cursor=` | Runs, cursor-paged | Viewer |
| GET | `/api/runs/{id}` | Run detail with task states | Viewer |
| POST | `/api/runs/{id}/cancel` | Cancel | Operator |
| POST | `/api/runs/{id}/retry-failed` | Re-run failed tasks | Operator |
| GET | `/api/runs/{id}/tasks/{key}/logs?attempt=` | Logs; `text/event-stream` for live tail | Viewer |
| GET · POST · PUT | `/api/host-groups` | Groups and their limits | Admin |
| POST | `/api/host-groups/{id}/pause` · `/resume` | Pause or resume dispatch | Operator |
| GET | `/api/hosts` | Hosts with status, slots, current tasks | Viewer |
| POST | `/api/hosts/{id}/drain` · `/undrain` | Drain a host | Operator |
| GET | `/api/queue/stats` | Queue depth and oldest wait per group | Viewer |

Example create request:

```json
POST /api/automations
{
  "name": "APAC nightly sales report",
  "workflow": { "key": "nightly-sales-report", "version": null },
  "parameters": { "region": "APAC", "recipients": ["sales-leads@example.com"] },
  "routing": { "hostGroup": "etl", "requiredLabels": [] },
  "priority": 60,
  "maxConcurrentRuns": 1,
  "concurrencyPolicy": "skip",
  "placementMode": "per_task",
  "runTimeout": "02:00:00",
  "triggers": [
    { "kind": "cron", "cron": "0 2 * * *", "timeZone": "Asia/Singapore", "misfirePolicy": "fire_once" },
    { "kind": "manual" }
  ]
}
```

### gRPC (agent → orchestrator)

```protobuf
service AgentService {
  rpc Register  (RegisterRequest)  returns (RegisterReply);   // name, group, labels, slots, version, hashes
  rpc Claim     (ClaimRequest)     returns (ClaimReply);      // long-poll up to 20 s; returns 0..free_slots leases
  rpc Heartbeat (HeartbeatRequest) returns (HeartbeatReply);  // renews leases; reply lists tasks to stop
  rpc AppendLogs(stream LogChunk)  returns (AppendLogsReply);
  rpc Start     (StartRequest)     returns (StartReply);      // Leased -> Running, fenced by token
  rpc Complete  (CompleteRequest)  returns (CompleteReply);   // FAILED_PRECONDITION when the token is stale
}

message TaskLease {
  string task_run_id = 1;
  string workflow_run_id = 2;
  string task_key = 3;
  string definition_hash = 4;
  int32  attempt = 5;
  int64  lease_token = 6;
  google.protobuf.Duration timeout = 7;
  bytes  params_json = 8;
  map<string, bytes> upstream_outputs_json = 9;
}
```

## 10. Roadmap and open questions

Phases follow the requirement priorities: phase 1 is every Must, phase 2 the Shoulds, phase 3 the Coulds plus event triggers. Each gate must pass before the next phase starts.

&#91;embedded content: roadmap · 3 phases, 2 gates\]

### Open questions

- [ ] Expected scale: runs per day, peak concurrent tasks, number of hosts? These set the targets in section 3.
- [ ] How long do tasks run — seconds or hours? Multi-hour tasks may need checkpointing.
- [ ] Do any workflows need every task on one host (local files)? That would make PinnedRun a Must.
- [ ] When a pinned host dies, should the run fail or re-pin to another host and restart from the start?
- [ ] Can a host belong to more than one group? v1 assumes one.
- [ ] Where do agents run — VMs, containers or Kubernetes? Kubernetes could later scale agents on queue depth.
- [ ] Which secret store do handlers read from (Azure Key Vault, HashiCorp Vault, Kubernetes secrets)?
- [ ] Should a failed notification task fail the whole run, or only be reported?
- [ ] UI framework: Blazor Server keeps the stack all-C#; React suits a separate frontend team.
