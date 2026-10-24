namespace Baton.Persistence.Migrations;

/// <summary>
/// Hand-written part of the baseline: everything EF does not model (ADR-0007). Run, task and history
/// tables are copied from <c>Schema/0001_baseline.sql</c>; the <c>Schema_matches_baseline</c>
/// integration test keeps the two in step. Never edit after release — add a new migration.
/// </summary>
public partial class Baseline
{
    private const string RuntimeTablesUp = """
        CREATE SEQUENCE lease_token_seq;

        CREATE TABLE workflow_runs (
          id              uuid PRIMARY KEY,
          automation_id   uuid REFERENCES automations(id),
          definition_id   bigint NOT NULL REFERENCES workflow_definitions(id),
          status          text NOT NULL CHECK (status IN ('pending','running','cancelling',
                          'succeeded','failed','cancelled','timed_out')),
          priority        smallint NOT NULL,
          host_group_id   uuid NOT NULL REFERENCES host_groups(id),
          pinned_host_id  uuid REFERENCES hosts(id),
          parameters      jsonb NOT NULL,
          trigger_kind    text NOT NULL,
          idempotency_key text NOT NULL UNIQUE,
          held_limits     text[] NOT NULL DEFAULT '{}',
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

        -- fillfactor leaves room for HOT updates; the low scale factor keeps autovacuum up with the
        -- constant status/lease updates on this table (M0 task 2.2).
        CREATE TABLE task_runs (
          id                    uuid PRIMARY KEY,
          workflow_run_id       uuid NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
          task_key              text NOT NULL,
          status                text NOT NULL CHECK (status IN ('waiting','queued','leased',
                                'running','retry_wait','succeeded','failed','skipped','cancelled')),
          trigger_rule          text NOT NULL,
          upstream_remaining    int NOT NULL,
          upstream_failed       int NOT NULL DEFAULT 0,
          upstream_unsuccessful int NOT NULL DEFAULT 0,
          attempt               int NOT NULL DEFAULT 0,
          max_attempts          int NOT NULL,
          timeout               interval NOT NULL,
          priority              smallint NOT NULL,
          effective_priority    smallint NOT NULL,
          host_group_id         uuid NOT NULL,
          required_labels       text[] NOT NULL DEFAULT '{}',
          definition_hash       char(64) NOT NULL,
          pinned_host_id        uuid,
          queued_at             timestamptz,
          not_before            timestamptz,
          lease_host_id         uuid,
          lease_token           bigint,
          lease_expires_at      timestamptz,
          cancel_requested      boolean NOT NULL DEFAULT false,
          output                jsonb,
          error                 jsonb,
          started_at            timestamptz,
          finished_at           timestamptz,
          UNIQUE (workflow_run_id, task_key)
        ) WITH (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.01);

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
          outcome     text,
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
          type            text NOT NULL,
          data            jsonb NOT NULL DEFAULT '{}',
          at              timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY (id, at)
        ) PARTITION BY RANGE (at);
        """;

    /// <summary>
    /// ADR-0011: one partition per UTC calendar month for each history table, from the current month
    /// to <c>months_ahead</c> months ahead. Idempotent; returns how many partitions it created. Called
    /// here so a fresh database accepts inserts at once, then daily by the orchestrator's
    /// <c>PartitionMaintenanceLoop</c> under an advisory lock.
    /// </summary>
    private const string PartitionMaintenanceUp = """
        CREATE FUNCTION ensure_history_partitions(months_ahead int DEFAULT 3) RETURNS int
        LANGUAGE plpgsql AS $fn$
        DECLARE
          parent      text;
          child       text;
          month_start timestamp;  -- UTC wall clock, so month arithmetic ignores the session TimeZone
          created     int := 0;
        BEGIN
          FOREACH parent IN ARRAY ARRAY['task_attempts', 'task_logs', 'run_events'] LOOP
            FOR i IN 0..months_ahead LOOP
              month_start := date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => i);
              child := parent || to_char(month_start, '"_y"YYYY"m"MM');
              IF to_regclass(child) IS NULL THEN
                EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                               child, parent,
                               month_start AT TIME ZONE 'UTC',
                               (month_start + interval '1 month') AT TIME ZONE 'UTC');
                created := created + 1;
              END IF;
            END LOOP;
          END LOOP;
          RETURN created;
        END
        $fn$;

        SELECT ensure_history_partitions();
        """;

    private const string RuntimeTablesDown = """
        DROP FUNCTION ensure_history_partitions(int);
        DROP TABLE run_events;
        DROP TABLE task_logs;
        DROP TABLE task_attempts;
        DROP TABLE task_runs;
        DROP TABLE workflow_runs;
        DROP SEQUENCE lease_token_seq;
        """;
}
