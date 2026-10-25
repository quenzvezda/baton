-- M2 (add-host-registry): agent credentials (ADR-0013) and drain kept apart from liveness
-- (design.md decisions 5 and 7). Applied after 0001_baseline.sql; mirrors migration HostRegistry.

CREATE TABLE agent_credentials (
  id            uuid PRIMARY KEY,
  host_group_id uuid NOT NULL REFERENCES host_groups(id) ON DELETE CASCADE,
  name          text NOT NULL,
  token_hash    bytea NOT NULL UNIQUE,         -- SHA-256 of the token; the token is never stored
  created_at    timestamptz NOT NULL DEFAULT now(),
  revoked_at    timestamptz,
  UNIQUE (host_group_id, name)
);

ALTER TABLE hosts
  ADD COLUMN drain_requested boolean NOT NULL DEFAULT false,
  ADD COLUMN credential_id   uuid REFERENCES agent_credentials(id) ON DELETE SET NULL;
