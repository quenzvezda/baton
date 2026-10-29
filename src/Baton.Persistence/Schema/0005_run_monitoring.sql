-- M5 (add-web-console-and-monitoring). Applied after 0004_automations.sql; mirrors migration RunMonitoring.

-- Newest-first runs list and its (created_at, id) cursor (OBS-01).
CREATE INDEX ix_runs_recent ON workflow_runs (created_at DESC, id DESC);
