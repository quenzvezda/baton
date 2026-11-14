-- M7 (add-operability-and-insight). Applied after 0006_scheduling_and_routing.sql; mirrors migration
-- OperabilityAndInsight.

-- Who changed configuration, when, and from what to what (AUT-11, design.md decision 12). No FK: entries
-- outlive their entity. Kept 1 year by daily maintenance.
CREATE TABLE audit_log (
  id            bigint GENERATED ALWAYS AS IDENTITY,
  at            timestamptz NOT NULL DEFAULT now(),
  actor_subject text NOT NULL,                                     -- OIDC sub
  actor_name    text NOT NULL,                                     -- as /api/me reports it
  entity_type   text NOT NULL,
  entity_id     text NOT NULL,                                     -- uuid, or workflow key for limits
  action        text NOT NULL,
  before        jsonb,
  after         jsonb,
  CONSTRAINT audit_log_pkey PRIMARY KEY (id),
  CONSTRAINT audit_log_entity_type_check
    CHECK (entity_type IN ('automation','host_group','agent_credential','workflow_limit'))
);
CREATE INDEX ix_audit_entity ON audit_log (entity_type, entity_id, at DESC, id DESC);
CREATE INDEX ix_audit_at ON audit_log (at);

-- Where an automation's Failed or TimedOut runs are reported (OBS-05, design.md decision 16). Webhook URL and
-- signing secret are Data Protection ciphertext (ADR-0015, ADR-0018).
CREATE TABLE notification_channels (
  id                  uuid NOT NULL,
  automation_id       uuid NOT NULL,
  kind                text NOT NULL,
  events              text[] NOT NULL,
  email_to            text[],
  webhook_url         bytea,
  webhook_url_display text,                                        -- scheme://host
  webhook_secret      bytea,
  CONSTRAINT notification_channels_pkey PRIMARY KEY (id),
  CONSTRAINT notification_channels_automation_id_fkey FOREIGN KEY (automation_id)
    REFERENCES automations (id) ON DELETE CASCADE,
  CONSTRAINT notification_channels_kind_check CHECK (kind IN ('email','webhook')),
  CONSTRAINT notification_channels_events_check
    CHECK (events <@ ARRAY['failed','timed_out'] AND cardinality(events) > 0),
  CONSTRAINT notification_channels_email_check CHECK ((kind = 'email') = (email_to IS NOT NULL)),
  CONSTRAINT notification_channels_webhook_check
    CHECK ((kind = 'webhook') = (webhook_url IS NOT NULL AND webhook_secret IS NOT NULL))
);
CREATE INDEX ix_notification_channels_automation ON notification_channels (automation_id);

-- The outbox: one row per channel to notify, written by the statement that ends the run (ADR-0018,
-- design.md decisions 17-19). The id is the opaque delivery id ntf_<id>.
CREATE TABLE notification_deliveries (
  id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  workflow_run_id uuid NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
  channel_id      uuid NOT NULL REFERENCES notification_channels(id) ON DELETE CASCADE,
  event           text NOT NULL CHECK (event IN ('failed','timed_out')),
  status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','sent','failed')),
  attempts        int NOT NULL DEFAULT 0,
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  locked_until    timestamptz,
  last_error      text,
  created_at      timestamptz NOT NULL DEFAULT now(),
  sent_at         timestamptz
);
CREATE INDEX ix_deliveries_due ON notification_deliveries (next_attempt_at) WHERE status = 'pending';
CREATE INDEX ix_deliveries_run ON notification_deliveries (workflow_run_id);
CREATE INDEX ix_deliveries_channel ON notification_deliveries (channel_id);

-- Re-runs in place (WF-10, design.md decisions 9-10) and the run's trace (OBS-06, decision 27).
ALTER TABLE workflow_runs
  ADD COLUMN reruns       int NOT NULL DEFAULT 0,
  ADD COLUMN trace_parent text;                                    -- W3C traceparent of the creating activity

-- Lease expirations in the last hour per group (OBS-04, design.md decision 24).
CREATE INDEX ix_attempts_lost ON task_attempts (finished_at) WHERE outcome = 'lost';
