-- M3 (add-task-dispatch): claim time of the current attempt. It is the started_at key of the attempt's
-- task_attempts row and the timeout backstop's fallback when Start never arrived (design.md
-- "Implementation notes"). Applied after 0002_host_registry.sql; mirrors migration TaskDispatch.

ALTER TABLE task_runs ADD COLUMN leased_at timestamptz;
