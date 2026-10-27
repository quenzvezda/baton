# ADR-0015: Webhook secrets encrypted with ASP.NET Core Data Protection

- **Status:** Proposed
- **Date:** 2026-10-27
- **Deciders:** @Ripan (M4 kickoff, 2026-10-27)
- **Spec:** §3 Security · AUT-04 · OpenSpec change `add-automations-and-run-lifecycle` (design.md decision 2)

## Context

A webhook trigger is authenticated by `X-Signature: sha256=<HMAC-SHA256 of the raw body>` computed with
the trigger's secret (AUT-04). Agent tokens are stored as SHA-256 hashes (ADR-0013) because the server only
compares a presented token. An HMAC is different: to recompute it, the server needs the secret itself. The
baseline column `automation_triggers.webhook_secret_hash` cannot work, and storing the secret in plain text
would expose every webhook to anyone who can read the database or a backup.

## Decision

Store each webhook secret encrypted with **ASP.NET Core Data Protection**, keeping the key ring in Postgres.

- The column becomes `automation_triggers.webhook_secret bytea`: `IDataProtector.Protect` of the secret's
  UTF-8 bytes, purpose `Baton.Webhooks.Secret.v1`.
- The key ring is persisted with `PersistKeysToDbContext<BatonDbContext>()` in table `data_protection_keys`
  (application name `baton`), so every Control Plane replica shares it and keys rotate automatically
  (90 days by default; old keys keep decrypting).
- The secret is `whsec_` + 32 random bytes (base64url). It is returned only by create, clone and
  `rotate-secret`; reads never return it.
- Production deployments encrypt the key ring at rest (`ProtectKeysWithCertificate` or a Key Vault/Vault
  key) through configuration; that choice belongs to the deployment, like the handler secret store (OQ-07).
- New package: `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (same release train as the
  pinned ASP.NET Core packages).

## Consequences

**Positive**
- A database dump alone does not reveal webhook secrets once the key ring is protected at rest.
- No new infrastructure: Postgres stays the only datastore (ADR-0003).

**Negative / risks**
- Losing the key ring makes every webhook secret unreadable → the secrets are rotated
  (`rotate-secret`) and senders updated; the key ring is backed up with the database.
- Without key-ring encryption configured, keys sit unencrypted next to the data → documented as a
  deployment requirement; Data Protection logs a warning at startup.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Hash the secret (as for agent tokens) | An HMAC cannot be verified from a hash. |
| Use the hash as the HMAC key | Anyone who reads the database can sign requests; non-standard for senders. |
| Plain text column | Secrets exposed in every dump and backup. |
| External secret store (Key Vault, Vault) | Not every deployment has one (OQ-07: Baton ships no store); a dependency on the request path. |

## References

- design spec §3 Security, §7 `automation_triggers`
- ADR-0013 (agent tokens), ADR-0003 (Postgres only)
