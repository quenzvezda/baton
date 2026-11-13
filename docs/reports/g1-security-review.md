# Gate G1 — security review

**Date:** 2026-11-14 · **Scope:** G1 Security criterion — "OIDC roles, agent credentials and webhook HMAC
reviewed; no secrets in parameters or logs" · **Method:** code read of `main` at `29344f8` against
ADR-0013, 0014, 0015, 0017 and `openspec/specs/` (access-control, automations, host-registry); greps over
tracked files. No dynamic testing.

**Result:** no Critical or High findings. Three Medium findings block the criterion until fixed or
accepted by the owner; Low and Info items are recorded for later.

## Findings

| ID | Severity | Area | Status |
| --- | --- | --- | --- |
| M1 | Medium | Agent credentials | Open |
| M2 | Medium | Webhook HMAC | Open — needs a spec delta (contract change) |
| M3 | Medium | Webhook secrets at rest | Open |
| L1 | Low | Secrets in parameters | Open |
| L2 | Low | Console sign-in | Open (unverified) |
| L3 | Low | gRPC limits | Open |
| L4 | Low | Agent credentials | Open |
| L5 | Low | Transport | Open |

### M1 — a token of a host's former group keeps control of the host after a move

`HostSql.MayActFor` (`src/Baton.Persistence/Hosts/HostSql.cs`) lets a credential act for a host when
the credential's *group* equals the group of the credential the host is bound to — not when it *is* that
credential — and ignores whether the bound credential was revoked. `AgentRegistry.RegisterSql` then
rebinds `credential_id` to the caller. After an Admin moves host `gpu-01` from `etl` to `gpu`, any live
`etl` token can re-register `gpu-01`, rebind it, and claim and complete `gpu` tasks (parameters, upstream
outputs, forged results). This widens ADR-0013's "a token is worth one group".

Fix: second branch `h.credential_id = @credentialId`; in `RegisterSql` overwrite `credential_id` only when
`h.host_group_id = @credentialGroupId`. Tests: a second token of the old group is refused; a revoked bound
credential is refused.

### M2 — webhook signatures replay indefinitely

The HMAC covers only the raw body (`src/Baton.Domain/Automations/WebhookSignature.cs`); `X-Delivery-Id`
deduplicates but is not signed, and there is no timestamp. One captured request replays forever with a
fresh delivery id, each creating a run (unbounded under the `allow` policy) until the secret is rotated.

Fix: sign `"{timestamp}.{deliveryId}.{body}"`, header `X-Signature: t=<unix>,v1=<hex>`, reject outside
±5 min of Postgres time. Changes the webhook contract ⇒ OpenSpec change (automations delta) and a note in
ADR-0015's successor.

### M3 — Data Protection key ring stored unencrypted next to the secrets it protects

`ControlPlane.cs` persists keys to the database with no `ProtectKeysWith*` and no configuration hook,
although ADR-0015 says production encrypts the key ring "through configuration". A `pg_dump` (the G1
backup test produces one) holds both `data_protection_keys` and the protected webhook secrets.

Fix: optional `Baton:DataProtection` section (certificate) that calls `ProtectKeysWithCertificate`; warn
at startup outside Development when unset. Until then: treat database backups as secret material
(runbook "Backup and restore").

### Low

- **L1** — Handlers resolve secret names through the agent's whole `IConfiguration`, which also holds
  `Agent:Token` and connection strings; an Operator could name `Agent:Token` as a "secret" and send it to
  a host they control. Resolve names only under a `Secrets` section (SDK helper), document in
  `src/Baton.Definitions/Workflows/README.md`. No shipped handler does this yet.
- **L2** — `LocalOrRoot` in `src/Baton.Web/Auth/AuthenticationSetup.cs` rejects `//` and `/\` but not
  control characters (`/%09/evil.example`), a probable open redirect after sign-in. Use
  `RedirectHttpResult.IsLocalUrl`; add tests.
- **L3** — The orchestrator accepts 64 MB gRPC messages before the auth interceptor runs. Lower server
  `MaxReceiveMessageSize` to ~2 MB (Complete output is ≤ 256 KB); keep 64 MB for sends.
- **L4** — Credentials are checked once per call; a Claim long-poll (≤ 60 s) and an open `AppendLogs`
  stream outlive a revocation. Later writes are refused, so the tasks are reaped and retried. Re-check
  in the claim loop.
- **L5** — No HTTPS redirection, HSTS, forwarded headers or `Cookie.SecurePolicy = Always` in API and
  console; the orchestrator has no TLS endpoint config outside Development. Left to deployment today.

### Info

- JWT `ClockSkew` is the default 5 min; consider 30–60 s.
- Unknown trigger ids answer 401 faster than bad signatures (timing oracle; irrelevant with UUIDv7).
- Register reveals that a host name exists in another group — to authenticated agents only.
- Handler logs and errors are visible to every Viewer; add "never log resolved secrets" to the
  Definitions README.
- The console's own Data Protection keys use the default per-machine store; multi-replica consoles need
  a shared, encrypted ring.
- Dev-only defaults (compose `baton/baton`, Keycloak `admin/admin`, realm users with password = name,
  `appsettings.Development.json` connection strings) are expected; no other secrets, keys or real
  hostnames in tracked files.

## Verified sound

- **OIDC roles.** All 34 REST endpoints declare a policy (Viewer reads; Operator automation, run and drain
  actions; Admin groups, tokens, host moves, limits); anonymous only `/healthz` and the webhook endpoint.
  Fallback policy requires Viewer and `EndpointInventoryTests` fails on unpoliced endpoints. Role claims
  are trusted only from `BatonRoleClaimsTransformation`; inbound claim mapping off; issuer, audience,
  lifetime and signature validated; `RequireHttpsMetadata` off only in Development. Console uses code +
  PKCE, HttpOnly SameSite=Lax cookie, tokens server-side.
- **Agent credentials.** 32 random bytes, SHA-256 hash stored (unique), shown once, looked up per call
  without cache, revoked tokens excluded; interceptor covers every RPC shape; wrong-group Register refused;
  every post-claim write fenced by lease token, status and expiry in one transaction (FAILED_PRECONDITION);
  agents refuse plaintext non-loopback orchestrator URLs.
- **Webhook HMAC.** `sha256=` HMAC of the raw body compared with `FixedTimeEquals` after length/hex
  checks; body capped at 1 MB and streamed; unknown trigger and bad signature both 401; secret 32 random
  bytes, Data Protection purpose `Baton.Webhooks.Secret.v1`, returned only by create, clone and rotate.
- **Secrets in logs.** No `[LoggerMessage]` template logs tokens, Authorization headers, secrets,
  connection strings or parameters; no HTTP logging or sensitive-data logging; Npgsql traces carry
  parameterized SQL text only; the agent token travels as gRPC metadata and is never logged.
