-- M6 (add-scheduling-and-routing-controls). Applied after 0005_run_monitoring.sql; mirrors migration
-- SchedulingAndRouting.

-- CatchUp runs the most recent N missed fire times, N set only for catch_up (AUT-08, design.md decision 7).
ALTER TABLE automation_triggers
  ADD COLUMN catch_up_limit smallint,
  ADD CONSTRAINT automation_triggers_catch_up_limit_check CHECK (catch_up_limit BETWEEN 1 AND 10),
  ADD CONSTRAINT automation_triggers_catch_up_limit_policy_check
    CHECK ((misfire_policy = 'catch_up') = (catch_up_limit IS NOT NULL));

-- The pin lives on the run (ORC-12, design.md decision 11).
ALTER TABLE workflow_runs
  ADD COLUMN placement_mode text NOT NULL DEFAULT 'per_task'
    CHECK (placement_mode IN ('per_task','pinned_run')),            -- copied from the automation at creation
  ADD COLUMN pin_labels     text[] NOT NULL DEFAULT '{}';          -- every label any task of the run requires
CREATE INDEX ix_runs_pinned ON workflow_runs (pinned_host_id) WHERE pinned_host_id IS NOT NULL;

ALTER TABLE task_runs
  ADD COLUMN pinned_run boolean NOT NULL DEFAULT false,            -- the task's run is PinnedRun
  DROP COLUMN pinned_host_id;

-- A run whose pinned host is lost fails (OQ-04, design.md decision 13).
ALTER TABLE workflow_runs DROP CONSTRAINT workflow_runs_cancel_reason_check,
  ADD CONSTRAINT workflow_runs_cancel_reason_check
    CHECK (cancel_reason IN ('user','replaced','timeout','pinned_host_lost'));
