# Design

## Context

See proposal.md. Constraints:
- ADR-0003: Postgres only.
- ADR-0006: leases and fencing; handlers are idempotent.
- ADR-0007: hand-written SQL on the claim, finish, reaper and admission paths.
- ADR-0008: `SKIP LOCKED` row loops, transaction-scoped advisory locks for singletons.
- ADR-0009: limit rows locked in key order.
- ADR-0011: 90-day history retention.
- ADR-0014: people are identified by OIDC claims.
- ADR-0015: secrets encrypted with Data Protection, key ring in Postgres.
- "One transaction per state transition" and "time comes from Postgres `now()`".

Design spec §4 "How a run flows" (step 6: "notifications fire"), §8 "Packages" (OpenTelemetry) and §9
(`POST /api/runs/{id}/retry-failed`, `GET /api/queue/stats`).

Today's state, from the code:

- **Run state.** `workflow_runs` holds `status`, `held_limits`, `limit_keys`, `run_timeout`,
  `deadline_at`, `blocked_on`, `cancel_reason`, `placement_mode`, `pin_labels` and `pinned_host_id`.
  `task_runs` has one row per task key (`UNIQUE (workflow_run_id, task_key)`), carrying:
  - the counters `attempt` and `max_attempts`;
  - the upstream counters `upstream_remaining`, `upstream_failed` and `upstream_unsuccessful`;
  - `output` and `error`.

  `task_attempts` and `task_logs` are keyed by `(task_run_id, attempt, …)` and partitioned by month.
  A run can therefore get new attempts without losing old ones.
- **Admission.** `RunAdmission.TryAdmitAsync` locks a Pending run with `SKIP LOCKED` and reserves its
  limits in key order. It then sets `running`, `started_at`, `deadline_at = now() + run_timeout` and
  `held_limits`, and calls `RunMaterializer.CreateTasksAsync`, all in one transaction. `CreateTasksAsync`
  inserts every task and assumes that none exist yet.
- **Finalization.** `RunFinalizer.FinalizeSql` is one statement. It ends the run, writes `run.<status>`
  and returns the held limits. Every path that ends a run calls it inside its own transaction: the
  finisher, the reaper and cancellation.
- **Configuration writes.** Automation, host group, agent token and workflow limit endpoints write
  through EF Core (`BatonDbContext`). Pause and resume use raw SQL inside an EF transaction. The actor's
  name comes from the `name`, `preferred_username` or `sub` claim (`MeEndpoints`). Automations are soft
  deleted, so runs keep referring to them.
- **Inbound webhooks.** `WebhookSignature` signs `t=<unix>,v1=<hex HMAC>` over
  `<t>.<delivery id>.<body>`. The delivery id travels in `X-Delivery-Id`. `WebhookSecrets` protects secrets
  with purpose `Baton.Webhooks.Secret.v1`. Only the API configures Data Protection
  (`SetApplicationName("baton")`, `PersistKeysToDbContext`, optional certificate through
  `KeyRingProtection`).
- **Telemetry.** `src/Shared/BatonTelemetry.cs` is wired into the API and the orchestrator only. It calls
  `AddAspNetCoreInstrumentation`, so it cannot be used as is by the agent, which is a Worker Service.
  - The agent and the web console have no telemetry.
  - `OrchestratorMetrics` has four counters and no gauges.
  - Handler logs go through the `Baton.Task` logger of the agent's `ILoggerFactory` and are shipped to the
    orchestrator (OBS-03).
- **Lease expiry.** `LeaseReaper.ReapAsync` closes expired leases with outcome `lost` and returns a total
  count (`ReapSummary.Lost`), not a count per group.

## Goals / Non-Goals

**Goals:**
- No behavior change for automations that have no notification channels and runs that are never re-run.
- No new lock-order edge on the hot path.
- Every new write that belongs to a state transition happens in that transition's transaction.
- Telemetry stays off unless an endpoint is configured.

**Non-Goals:**
- Exactly-once notifications (receivers deduplicate on the delivery id).
- Changing an audit entry.
- Auditing reads.
- Per-replica deduplication of gauges (alerts aggregate with `max`).
- Tracing the orchestrator's loops tick by tick.

## Decisions agreed with the owner (2026-11-14)

1. **WF-10 re-runs in place.** A re-run keeps the run's id, so:
   - attempt numbers continue on the same `task_runs` rows;
   - earlier attempts and logs stay attached;
   - `IdempotencyKey` (`runId:taskKey`) is unchanged, so to a handler a re-run looks like another retry,
     which at-least-once already requires it to handle.

   A linked new run was rejected. It would copy the succeeded tasks' outputs into a second run, split
   one piece of work across two run ids, and give handlers a new idempotency key for work that may
   already have happened. A run that reached a final state can therefore become active again. The timeline
   records this as `run.rerun_requested`, and `workflow_runs.reruns` counts it.
2. **Re-run rules.**
   - Only Failed and TimedOut runs can be re-run (409 otherwise). Cancelled runs are excluded because a
     person chose to stop them.
   - The tasks that reset are every task that is not Succeeded and is affected by the failure (decision 8).
   - Reset tasks get a fresh retry budget. Attempt numbers continue.
   - The run keeps the same definition version.
   - The run goes back to Pending and through admission, so limits, `maxConcurrentRuns`, priority and
     paused groups apply as for any run. The deadline is counted from the new start, and a PinnedRun run
     pins again.
   - Endpoint: `POST /api/runs/{id}/retry-failed`, Operator.
3. **AUT-11 scope and retention.** Audited: automations (with triggers, notification channels,
   enable/disable, clone, delete, secret rotation), host groups (with limits, pause/resume), agent
   credentials (issue, revoke). Entries are written in the change's transaction. Secrets are redacted.
   Entries are kept for **1 year**, a separate retention from ADR-0011's 90 days, because audit answers
   questions that come later than run debugging does. Read with `GET /api/audit` (Viewer) and a history
   view in the console.
4. **OBS-05 uses a transactional outbox.** `RunFinalizer` writes one delivery row per matching channel in
   the statement that ends the run. Orchestrator replicas deliver with `SKIP LOCKED` and retry with
   backoff: 5 attempts, about 1 h in total. This keeps "no update then publish" without a broker
   (ADR-0018).
5. **Email through MailKit, Mailpit for local development** (ADR-0019). Outbound webhooks:
   - are signed with the inbound format;
   - store their URL encrypted with Data Protection, because URLs such as Slack's carry a token;
   - accept HTTPS only.
6. **OQ-08: a notification never changes a run's outcome.** A delivery that exhausts its attempts is
   recorded (`notification.failed` run event, `baton.notifications.failed` metric) and nothing more.
   Workflow-authored notify tasks such as `notify-failure` are ordinary tasks and keep their WF-06
   semantics.
7. **OBS-04/06 scope.**
   - `GET /api/queue/stats` and a Queue page.
   - Telemetry wired into the agent and the console.
   - Trace context from agent to orchestrator, and a span per handler attempt.
   - Handler logs exported.
   - Gauges for queue depth, oldest wait and active leases, and a lease-expiration counter, so G2's alerts
     can be built.

**Proposed here, to confirm in review:**

- **Workflow run limits are audited too** (`PUT`/`DELETE /api/workflows/{key}/limit`, Admin). They are
  configuration that changes admission, like host-group limits, and cost one more call site.
- **Deleted automations notify nobody.** A run that outlives its soft-deleted automation finishes
  silently. Whoever deleted the automation has opted out of hearing about it.
- **The orchestrator shares the Data Protection key ring** to decrypt webhook URLs and signing secrets
  (decision 23). This widens ADR-0015, which only names the API. ADR-0018 records it.

## Decisions

### Re-run (WF-10)

8. **Re-run planning is pure domain code.** `Baton.Domain.Runs.RerunPlan.Plan(graph, statuses)` returns,
   per task, either *keep* or *reset* with new counters and an initial state.
   - The reset set R is every Failed or Cancelled task, plus every Skipped task that has an upstream task
     in R, applied transitively in topological order.
   - Succeeded tasks and every other Skipped task are kept.
   - For a task in R:
     - `upstream_remaining` = its upstream tasks in R;
     - `upstream_failed` and `upstream_unsuccessful` are counted over the kept upstream tasks (a kept
       Skipped upstream is unsuccessful);
     - its initial state is Waiting when `upstream_remaining > 0`, otherwise `TriggerRules.Decide` on the
       counters gives Queued or Skipped.
   - A Skipped result propagates through `DagAdvance` exactly as at run time, so a rule such as OneFailed
     with only successful upstream tasks skips again instead of running.

   Unit-tested on the spec scenarios: last task failed, failure-only task, diamond, timed-out run with
   Cancelled Waiting tasks, Skipped task with kept upstream.

9. **Request transaction.** `RunRerun.RequestAsync(runId, actor)` runs in one transaction:
    - it locks the run `FOR UPDATE` (not `SKIP LOCKED`: an Operator request should wait the few ms a
      finisher holds the row) and checks `status IN ('failed','timed_out')`, otherwise 409;
    - it sets `status = 'pending'`, `reruns = reruns + 1`, and clears `finished_at`, `cancel_reason`,
      `pinned_host_id`, `blocked_on` and `deadline_at`;
    - it writes `run.rerun_requested` with `{ actor, rerun: n }`.

    Tasks are not touched here: until admission they stay final, so neither claims nor the reaper see
    them. `held_limits` is already empty, because the finalizer released it. No limit or task row is
    locked, so no new lock-order edge appears. The response is the run detail (202).
10. **Re-arming at admission.** `RunAdmission.TryAdmitAsync` reads `reruns`. When it is greater than 0,
    it calls `RunMaterializer.RearmTasksAsync` instead of `CreateTasksAsync`, in the same transaction.
    `RearmTasksAsync`:
    - locks the run's task rows (`FOR UPDATE`, in task-key order, after the run row, which matches the
      finisher's run → task order);
    - computes `RerunPlan`;
    - updates each reset task: new status and counters, `max_attempts = attempt + <definition max
      attempts>`, `effective_priority = priority`, `queued_at` set when Queued, and `output`, `error`,
      `finished_at`, `not_before`, the lease columns and `cancel_requested` cleared;
    - writes `task.queued` / `task.skipped` events with `attempt + 1`, rewrites `pin_labels`, and sends
      `NOTIFY` for the group.

    Kept tasks are not written. Upstream outputs reach the reset tasks through the existing lease query,
    which reads `output` from upstream rows. The run's original `started_at` is overwritten by the new
    start. The first start stays in the timeline. A run that is re-run but never admitted can be cancelled
    as any Pending run.
11. **Concurrency.** A re-run request racing a cancel, or two requests at once, serialize on the run row.
    The second request sees `pending` and gets 409. The integration tests use concurrent actors:
    - two simultaneous re-run requests give one 202 and one 409;
    - a re-run admitted while a claimer polls leases each reset root exactly once;
    - a re-run of a run whose last lease is being reaped is either rejected or re-arms after the reap.

### Audit trail (AUT-11)

12. **Audit table.** Migration:
   ```sql
   CREATE TABLE audit_log (
     id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
     at timestamptz NOT NULL DEFAULT now(),
     actor_subject text NOT NULL,        -- OIDC sub
     actor_name text NOT NULL,           -- name / preferred_username / sub, as /api/me
     entity_type text NOT NULL CHECK (entity_type IN ('automation','host_group','agent_credential','workflow_limit')),
     entity_id text NOT NULL,            -- uuid, or workflow key for limits
     action text NOT NULL,               -- create|update|delete|enable|disable|clone|pause|resume|issue|revoke|rotate_secret|set|remove
     before jsonb, after jsonb);
   CREATE INDEX ix_audit_entity ON audit_log (entity_type, entity_id, at DESC, id DESC);
   CREATE INDEX ix_audit_at ON audit_log (at);
   ```
   The table is not partitioned: the volume is a few thousand rows a year. It has no FK, so entries outlive
   their entity, and it is written through EF Core because it is not on the hot path (ADR-0007).
13. **Writing entries.** `AuditWriter.Add(db, entityType, entityId, action, before, after)` adds the row
    to the request's `BatonDbContext`, so it commits with the change in the same `SaveChangesAsync`. Raw-SQL
    endpoints (pause/resume) call it inside their existing EF transaction. The actor comes from
    `ClaimsPrincipal` through one helper shared with `/api/me`.
    - `before` and `after` are the endpoint's own response DTO, serialized with the API's JSON options, so
      the audit shows what the API shows. `before` is read before the change is applied.
    - A rejected change (validation, 412, 404) never reaches `SaveChanges`, so it writes no entry.
    - Every endpoint listed in decision 3 gets an integration test asserting its entry.
14. **Redaction.** DTOs already omit webhook secrets and agent tokens. Notification channels show the URL
    as `scheme://host` (decision 16). Actions that issue or rotate a secret record `{ triggerId }` or
    `{ channelId }` only. A unit test serializes each audited DTO and fails if a property marked
    `[Secret]`, or a value matching `whsec_` or the agent token prefix, appears.
15. **Read API and retention.**
    - `GET /api/audit?entityType=&entityId=&from=&to=&cursor=&limit=` (Viewer) returns
      `{ items: [{ id, at, actor, entityType, entityId, action, before, after }], nextCursor }`.
    - The cursor is `(at, id)` descending, like `RunCursor`. `entityType` is required, and `entityId` is
      optional, so "all host-group changes" works.
    - `PartitionMaintenanceLoop`, which already runs daily on one replica, also runs
      `DELETE FROM audit_log WHERE at < now() - interval '1 year'` in batches of 10,000.

### Notifications (OBS-05)

16. **Channel storage.** Migration:
    ```sql
    CREATE TABLE notification_channels (
      id uuid PRIMARY KEY, automation_id uuid NOT NULL REFERENCES automations(id) ON DELETE CASCADE,
      kind text NOT NULL CHECK (kind IN ('email','webhook')),
      events text[] NOT NULL CHECK (events <@ ARRAY['failed','timed_out'] AND cardinality(events) > 0),
      email_to text[],                    -- 1..20 addresses, email only
      webhook_url bytea,                  -- Data Protection, purpose Baton.Notifications.WebhookUrl.v1
      webhook_url_display text,           -- scheme://host, for reads and audit
      webhook_secret bytea,               -- Data Protection, purpose Baton.Notifications.Secret.v1
      CHECK ((kind = 'email') = (email_to IS NOT NULL)),
      CHECK ((kind = 'webhook') = (webhook_url IS NOT NULL AND webhook_secret IS NOT NULL)));
    ```
    API:
    - `AutomationRequest`/`AutomationResponse` gain `notifications: [{ id?, kind, on: ["failed","timed_out"],
      recipients?, url? }]`.
    - Channels are matched by `id` on update. An omitted `url` keeps the stored one. A new webhook channel
      returns `signingSecret` once.
    - `POST /api/automations/{id}/notifications/{channelId}/rotate-secret` (Operator) mirrors the trigger
      endpoint.
    - Clone copies email channels and drops webhook channels, because a clone must not share a secret.
      The clone response says how many were dropped.
    - Validation: absolute `https` URL (`http` only when `Baton:Notifications:AllowInsecureWebhooks` is
      true, for development and tests), RFC 5322 addresses, at most 10 channels per automation.
17. **Delivery rows.** Migration:
    ```sql
    CREATE TABLE notification_deliveries (
      id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,   -- webhook delivery id: ntf_<id>
      workflow_run_id uuid NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
      channel_id uuid NOT NULL REFERENCES notification_channels(id) ON DELETE CASCADE,
      event text NOT NULL, status text NOT NULL CHECK (status IN ('pending','sent','failed')),
      attempts int NOT NULL DEFAULT 0, next_attempt_at timestamptz NOT NULL DEFAULT now(),
      locked_until timestamptz, last_error text, created_at timestamptz NOT NULL DEFAULT now(),
      sent_at timestamptz);
    CREATE INDEX ix_deliveries_due ON notification_deliveries (next_attempt_at) WHERE status = 'pending';
    ```
    The id is an identity column, like `run_events`, not a UUIDv7 (ADR-0010). The finalizer inserts a
    variable number of rows in one SQL statement, and Postgres 16 has no `uuidv7()`. A sequence keeps that
    single statement without a hand-written SQL UUID function, and the id never leaves the platform
    except as the opaque delivery id `ntf_<id>`. Runs purged after 90 days take their deliveries with them.
    Removing a channel drops its pending deliveries. A soft-deleted automation keeps its channels but
    schedules nothing (decision 18).
18. **Enqueue in the finalizer.** `FinalizeSql` gains `r.automation_id` in `ended` and a CTE:
    ```sql
    notify AS (
      INSERT INTO notification_deliveries (workflow_run_id, channel_id, event, status)
      SELECT e.id, c.id, e.status, 'pending'
      FROM ended e JOIN automations a ON a.id = e.automation_id AND a.deleted_at IS NULL
      JOIN notification_channels c ON c.automation_id = a.id AND e.status = ANY (c.events)
      WHERE e.status IN ('failed','timed_out'))
    ```
    It is in the same statement and transaction as the run's end, so a run that ends always has its
    deliveries, and a rolled-back finish has none. No lock is taken on channel rows (plain read), so no
    lock-order edge is added. A run with no channels pays one index probe.
19. **Delivery loop.** `NotificationLoop : PeriodicLoop`, 5 s, runs on every replica (ADR-0008 row loop).
    Each tick claims up to 20 due rows in a short transaction:
    `UPDATE … SET locked_until = now() + interval '2 minutes', attempts = attempts + 1 WHERE id IN (SELECT id …
    WHERE status = 'pending' AND next_attempt_at <= now() AND (locked_until IS NULL OR locked_until < now())
    ORDER BY next_attempt_at LIMIT 20 FOR UPDATE SKIP LOCKED) RETURNING …`.
    - It commits, then sends outside any transaction: no row lock is held across network I/O, which a
      `FOR UPDATE` held during a 10 s webhook would cause.
    - Each result is written in its own transaction, guarded by `attempts = @attempts`:
      - success sets `sent` and `sent_at`, and writes `notification.sent`;
      - a retryable failure sets `next_attempt_at = now() + backoff(attempts)`, `locked_until = NULL` and
        `last_error`;
      - the 5th failure sets `failed` and writes `notification.failed` with the error.
    - A replica that dies mid-send leaves the row locked for 2 minutes, after which another replica retries
      it: at least once.
    - The backoff is `NotificationSchedule.Delay(attempt)` in `Baton.Domain`: 1, 5, 15 and 30 minutes, ±10 %
      jitter, unit-tested.
    - Concurrent-actor integration test: two loops, 50 due deliveries, each sent exactly once when every
      send succeeds.
20. **Content.** One `NotificationMessage` built from the run detail query:
    - automation name and id, run id, workflow `key@version`, outcome, `startedAt`, `finishedAt`,
      `reruns`;
    - failed tasks with key, attempt and error message, truncated to 1,000 characters each, at most 20
      tasks;
    - `consoleUrl` when `Baton:Notifications:ConsoleBaseUrl` is set.

    Email: subject `[Baton] <automation> <outcome>`, with a plain-text and an HTML body rendered by a small
    template in code (no template engine). Webhook body: `{ "id", "type": "run.failed" | "run.timed_out",
    "occurredAt", "run": { … } }`.
21. **Webhook sending.** It uses a named `HttpClient` with `AllowAutoRedirect = false`, a 10 s timeout and no
    resilience handler, because the loop owns retries. Headers:
    - `Content-Type: application/json`;
    - `X-Delivery-Id: <delivery id>`;
    - `X-Signature: WebhookSignature.Compute(secret, now, id, body)`, where `now` is the orchestrator
      clock's Unix time. The receiver's tolerance is ±5 min, and the signature is computed per attempt,
      which makes this the one deliberate use of a local clock: it is a header for a third party, not
      platform state;
    - `User-Agent: Baton/<version>`.

    2xx means sent. 408, 425, 429, 5xx, timeouts and connection errors are retried. Any other 4xx fails at
    once, because the receiver rejected the delivery and retrying will not help. The same holds when the
    stored URL or secret can no longer be decrypted.
22. **Email sending.** `SmtpNotificationSender` uses MailKit (ADR-0019) with `Baton:Notifications:Smtp`
    (`Host`, `Port`, `Security`: `StartTls|SslOnConnect|None`, `UserName`, `Password` from the
    environment, `From`). One connection is opened per tick batch. Transient SMTP failures (4xx replies,
    I/O errors) are retried. 5xx replies fail at once. If SMTP is not configured, email deliveries fail at
    once with "email delivery is not configured", and startup logs a warning when email channels exist.
    Options are validated at startup.
23. **Key ring in the orchestrator.** `KeyRingProtection` and the Data Protection registration move from the
    API into `src/Shared/BatonDataProtection.cs` (linked source, like `BatonTelemetry`). Both processes
    call `AddBatonDataProtection(configuration)` with application name `baton`, the same table and the same
    certificate settings. The orchestrator only unprotects. `NotificationSecrets` (purposes in decision 16)
    lives in `Baton.Persistence`, so the API (protect) and the orchestrator (unprotect) share it.

### Queue statistics (OBS-04)

24. **Query.** `QueueStats.ReadAsync` in `Baton.Persistence` (Dapper, hand-written) does one round trip over
    `host_groups` with lateral aggregates:
    - queued count and `now() - min(queued_at)` from `task_runs WHERE status = 'queued'`, which uses
      `ix_tasks_dispatch`;
    - active leases from `status IN ('leased','running')`, which uses `ix_tasks_leases`;
    - pending runs from `ix_runs_admission`;
    - lease expirations from
      `task_attempts a JOIN task_runs t ON t.id = a.task_run_id WHERE a.outcome = 'lost' AND a.finished_at >= now() - interval '1 hour'`.
      The migration adds `CREATE INDEX ix_attempts_lost ON task_attempts (finished_at) WHERE outcome = 'lost'`,
      which is created on every partition, existing ones included.

    `GET /api/queue/stats` (Viewer) returns `[{ hostGroupId, name, paused, queued, oldestWaitSeconds?,
    pendingRuns, activeLeases, leaseExpirationsLastHour }]` ordered by name. `EXPLAIN` against the G1 load
    profile's data is captured in the task's report (task 4.1).
25. **Console.** A `/queue` page refreshes every 5 s with a `PeriodicTimer` in the component and is disposed
    with the circuit. Each group row links to the runs list filtered by group and to the group's history.

### Telemetry (OBS-06)

26. **Shared wiring split.** `BatonTelemetry.AddBatonTelemetry(builder, serviceName)` keeps the common part:
    resource, HttpClient and Npgsql instrumentation, `Baton.*` meters and sources, logs, and OTLP when
    `OTEL_EXPORTER_OTLP_ENDPOINT` is set. `AddBatonAspNetCoreTelemetry` adds the ASP.NET Core
    instrumentation and the health-probe filter.
    - API, orchestrator and web call both.
    - The agent calls the common part only. The agent does not use Npgsql, and its Npgsql instrumentation
      is a no-op.
    - Resource attributes: `service.name` (`control-plane-api`, `orchestrator`, `agent`, `web`),
      `service.version` (assembly informational version), `service.instance.id` (agent host name,
      otherwise machine name).
    - `Baton.Web` and `Baton.Agent` link the file and reference the OpenTelemetry packages already pinned
      in `Directory.Packages.props`. No new package.
27. **Trace context per run.** Migration: `workflow_runs.trace_parent text`. It holds the W3C `traceparent`
    of `Activity.Current` when `RunCreator` creates the run.
    - For Run now and webhooks that activity is the ASP.NET Core request.
    - For cron, `TriggerLoop` starts an activity `baton.trigger.fire` from `ActivitySource("Baton.Orchestrator")`
      around each firing, which makes it the root of that run's trace.

    The lease query returns the run's `trace_parent`, and `agent.proto` adds `string trace_parent = 10;` to
    `TaskLease`. The change is additive: an old agent ignores the field and a new agent tolerates it being
    empty.
28. **Handler span.** `TaskExecutor` starts `ActivitySource("Baton.Agent").StartActivity("baton.task.execute",
    ActivityKind.Internal, parentContext: <parsed trace_parent>, links: [current claim activity])`.
    - Tags: `baton.run.id`, `baton.task.key`, `baton.attempt`, `baton.host_group`, `baton.definition_hash`,
      `baton.outcome`.
    - On failure: `ActivityStatusCode.Error` and `AddException`.
    - It wraps handler execution only, not lease renewals.
    - The agent → orchestrator gRPC calls already carry `traceparent`: grpc-dotnet sends through
      `HttpClient`, whose instrumentation propagates context, and the orchestrator's ASP.NET Core
      instrumentation continues it. No gRPC-specific instrumentation package (they are pre-release) is
      needed.
    - Long-poll `Claim` spans of up to 20 s are expected. Sampling stays the deployment's choice
      (`OTEL_TRACES_SAMPLER`).
29. **Handler logs.** The `Baton.Task` logger already comes from the agent's `ILoggerFactory`. Once the agent
    adds the OpenTelemetry logging provider, those records are exported. `TaskExecutor` wraps execution in
    `logger.BeginScope({ baton.run.id, baton.task.key, baton.attempt })` with the handler span current, so
    records carry the attributes and the trace and span ids. Shipping to the orchestrator (OBS-03) is
    unchanged. Tests use the BCL's `ActivityListener` and `MeterListener` and a capturing `ILoggerProvider`,
    with no in-memory exporter package.
30. **Metrics.** Added to `OrchestratorMetrics` (meter `Baton.Orchestrator`):
    - `baton.queue.depth` `{task}`, observable gauge, tag `baton.host_group`;
    - `baton.queue.oldest_wait` `s`, observable gauge, tag `baton.host_group`;
    - `baton.leases.active` `{lease}`, observable gauge, tag `baton.host_group`;
    - `baton.leases.expired` `{lease}`, counter, tag `baton.host_group`. `ReapSummary` gains `LostByGroup`,
      and the reaper adds it after each pass;
    - `baton.notifications.sent` / `baton.notifications.failed` `{notification}`, counters, tag
      `baton.notification.kind`.

    The gauges read a snapshot that `QueueMetricsLoop : PeriodicLoop` refreshes every 15 s with
    `QueueStats.ReadAsync`, because observable gauge callbacks must not query the database. Every replica
    reports. Alerts use `max by (baton.host_group)`.

## Risks / Trade-offs

- [A re-run of a run near the 90-day retention edge loses old attempts when their month's partition is
  dropped] → acceptable. The run row and the task outputs survive (they are not partitioned), and only the
  history of earlier attempts goes. The run detail already tolerates missing attempt rows.
- [Re-armed tasks call handlers again with the same idempotency key, which a handler that wrongly treats
  "key seen" as "succeeded" would skip] → the SDK documentation for `TaskContext.IdempotencyKey` says that
  the key identifies the unit of work, not a single success. The PR calls this out to workflow authors.
- [An SSRF-style webhook URL pointing at an internal service] → only Operators configure channels, HTTPS
  is required and redirects are not followed. `Baton:Notifications:AllowedWebhookHosts` (optional
  allowlist, empty means any) lets a deployment restrict targets. It is documented in the runbook.
- [The key ring is now needed by the orchestrator] → the same certificate configuration as the API.
  Without it, webhook deliveries fail with a decryption error that is visible in the timeline and the
  metric, not silently. The deployment docs list the setting for both processes.
- [Gauges from every replica double count in a sum] → documented, and alerts use `max`.
- [The audit `before`/`after` follows the DTO shape, which can change between versions] → acceptable.
  Entries are read by people, and the console's diff view compares whatever keys are present.
- [A notification storm when many runs fail at once] → the loop sends at most 20 per tick per replica, and
  the backlog is visible as pending deliveries. Rate limiting per channel is left for when it is needed.

## Migration Plan

One EF migration, `OperabilityAndInsight`, with hand-written SQL like earlier ones:
- `audit_log` and its indexes;
- `notification_channels`, `notification_deliveries` and `ix_deliveries_due`;
- `workflow_runs.reruns int NOT NULL DEFAULT 0` and `workflow_runs.trace_parent text`;
- `ix_attempts_lost`.

Every column has a default or is nullable, so existing rows need no backfill.
- **Rollback.** The Down migration drops the new objects. Pending deliveries and audit entries are lost.
- **Rollout order.** Database, then orchestrator and API (both need the key ring settings), then agents.
  Old agents keep working without handler spans.
- **Contracts.** The REST and gRPC changes are additive.
- **Local development.** `docker-compose.yml` gains a `mailpit` service (SMTP 1025, UI 8025), and
  `appsettings.Development.json` points SMTP at it.
