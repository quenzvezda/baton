# Design

## Context

See proposal.md. Constraints: ADR-0015 (secrets encrypted with Data Protection, unchanged), "Time comes
from Postgres `now()`", archived `add-automations-and-run-lifecycle` design.md decisions 2–3 (signature of
the raw body; `X-Delivery-Id` deduplicates retries through the run idempotency key).

## Goals / Non-Goals

**Goals:** a captured request cannot create a second run, ever; senders can implement the scheme with
`openssl` in one line, as today.

**Non-Goals:** accepting the old format during a migration; nonce storage beyond the existing run
idempotency key.

## Decisions

1. **Header format** `X-Signature: t=<unix seconds>,v1=<lowercase or uppercase hex>`, the layout senders
   know from common webhook providers. The signed payload is the ASCII bytes of `<t>.<delivery id>.`
   followed by the raw body. Several `v1=` entries are accepted (any match verifies), so a sender can sign
   with an old and a new secret around a rotation; unknown keys are ignored for forward compatibility.
2. **Tolerance 5 minutes either side**, fixed (`WebhookSignature.Tolerance`). The server's time is
   Postgres `now()`, read in the request, never the API host's clock.
3. **Order of checks unchanged:** body size (413) → trigger and signature, now including timestamp and
   delivery id (401, same answer for unknown trigger, bad signature or stale time) → delivery id present
   (400) → trigger enabled (409) → fire. A missing delivery id is signed as the empty string, so a sender
   that forgot it gets 400 after a valid signature, as before.
4. **Replay is closed by what already exists:** inside the window the delivery id is fixed by the
   signature and the run idempotency key (`…:delivery:<id>`) returns the first run (Duplicate); outside
   it, the timestamp fails. Idempotency keys live with runs for 90 days (add-history-retention), far
   beyond 5 minutes.
5. **No ADR.** ADR-0015 covers how secrets are stored, which does not change; the signature scheme is
   behavior and lives in the automations spec.

## Risks / Trade-offs

- [Sender clock skew] → 5 minutes covers ordinary NTP drift; a rejected sender sees 401 and the runbook
  names clock skew as the first thing to check.
- [Breaking change] → documented in README and the PR; the console shows the secret only, so it needs no change.
