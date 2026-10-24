-- =====================================================================
-- Baton baseline schema — copied verbatim from design spec §7 (2026-10-22).
-- Reference only: the EF Core initial migration (M0) must produce exactly this,
-- with partial indexes, partitioning and fillfactor via migrationBuilder.Sql().
-- Do not edit here without an OpenSpec change / ADR; edit the migration instead.
-- =====================================================================

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
