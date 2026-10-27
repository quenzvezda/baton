# Design

## Context

See proposal.md. Constraints: ADR-0008 (SKIP LOCKED trigger loop, advisory-locked admission),
ADR-0009 (limit rows, key-ordered locking, pass-over admission). SQL shape for due triggers in
design spec §7. Concurrency policy table in §6 "Limits".

## Goals / Non-Goals

**Goals:** cron runs created within 5 s of fire time; exactly one run per firing across replicas;
admission never deadlocks and never lets a big run block smaller ones that fit.

**Non-Goals:** misfire catch-up (M6, v1 behaves as FireOnce); notifications (M7).

## Decisions

- **Idempotency key.** `automation:{id}:trigger:{triggerId}:{fireTimeUtc:O}` for cron/webhook; Run now
  uses a client-supplied or generated request id. `INSERT … ON CONFLICT (idempotency_key) DO NOTHING`.
- **Next fire.** Computed with Cronos in the trigger's IANA zone, stored as UTC `next_fire_at`; on DST gaps
  Cronos semantics apply (documented). After downtime v1 fires once and moves to the next future time.
- **Concurrency policy** evaluated in the trigger transaction by counting active runs of the automation
  (locking the `automation:{id}` row): Skip ⇒ log `run.skipped` event only; Queue ⇒ Pending; Replace ⇒
  cancel oldest active run then create; Allow ⇒ create and mark the run as not holding the automation limit.
- **Latest version.** "Follow latest" resolves at run creation, so a run never changes version later.
- **Admission.** Per group, read up to 100 Pending runs by `priority DESC, created_at`; for each, lock its
  limit rows in key order and reserve if all have room, else pass over. On success: Running, `deadline_at`,
  create task runs (roots Queued) and NOTIFY.
- **Finalization.** When the last task becomes final: Failed if any task Failed; otherwise Succeeded
  (Skipped alone never fails a run); Cancelled/TimedOut when the run was cancelling. Release `held_limits`.
- **Webhooks.** `X-Signature: sha256=<hex HMAC of raw body>`; secret shown once at creation, stored
  encrypted (decision 2 below); constant-time compare; the body is not used in M4 (decision 7).
- **Optimistic concurrency.** `row_version` exposed as ETag; `If-Match` mismatch ⇒ 412.

## Decisions agreed with the owner (2026-10-27)

1. **OQ-07: Baton ships no secret store.** Parameters carry the *name* of a secret (`"smtpPasswordSecret":
   "reports/smtp"`), never its value. Handlers resolve the name through the agent's standard
   `IConfiguration`, whose providers (environment variables, Azure Key Vault, HashiCorp Vault, Kubernetes
   secrets mounted as files) are chosen per deployment. No SDK change; the guidance is in
   `src/Baton.Definitions/Workflows/README.md` "Secrets".
2. **Webhook secrets are encrypted, not hashed** (ADR-0015). Verifying an HMAC needs the secret itself, so
   `automation_triggers.webhook_secret_hash` becomes `webhook_secret`: the secret protected with ASP.NET
   Core Data Protection, whose key ring lives in Postgres (`data_protection_keys`). The secret
   (`whsec_` + 32 random bytes, base64url) is returned only by create, clone and rotate; the HMAC key is
   its UTF-8 bytes.
3. **Webhook deliveries are deduplicated by `X-Delivery-Id`.** The header is required (missing ⇒ 400,
   at most 200 characters); the idempotency key is `automation:{id}:trigger:{triggerId}:delivery:{id}`.
   A repeated delivery returns 202 with the run it created the first time.
4. **Workflow run limits are set by Admins:** `PUT /api/workflows/{key}/limit` `{ "maxConcurrentRuns": n }`
   upserts `concurrency_limits` row `workflow:{key}`, `DELETE` removes it (unlimited), `GET` reads it.
   The limit is operational, not part of the definition, so it never changes a hash. UI in M5.
5. **Automations are soft-deleted.** `automations.deleted_at` is set; the name is unique among automations
   not deleted (partial unique index, still named `automations_name_key`). Runs keep their
   `automation_id`; pending and running runs of a deleted automation run to completion; its triggers stop.
6. **The cancellation grace period is enforced.** `task_runs.cancel_requested_at` records the request; the
   reaper closes a Leased or Running task whose cancellation was requested 30 s ago or more as Cancelled
   (never retried), even while its host keeps heartbeating. The next heartbeat reports it lost, so the
   agent drops the handler's result.

Defaults taken without objection:

7. The webhook body is ignored in M4; parameter overrides come with AUT-12 (M8).
8. Run now and webhooks go through the concurrency policy. Run now on a disabled automation ⇒ 409;
   Run now under Skip at the limit ⇒ 409 with the skipped fire recorded; a webhook under Skip at the
   limit ⇒ 200 `{ "skipped": true }` so the sender does not retry. A webhook for a disabled automation or
   trigger ⇒ 409. Unknown trigger and bad signature both ⇒ 401, so trigger ids cannot be probed.
9. "Active" runs are `pending`, `running` and `cancelling`; the policy compares their count with
   `max_concurrent_runs`. Replace cancels the oldest active run that is not already cancelling; a Pending
   one becomes Cancelled at once.
10. Run now takes an optional `Idempotency-Key` header (at most 200 characters); without it every call is a
    new request. Key: `automation:{id}:run-now:{value}`.
11. Cron expressions have 5 fields (no seconds), parsed by Cronos; the time zone is an IANA id.
    Webhook URL: `POST /api/webhooks/{triggerId}`, anonymous, authenticated by the signature only.
    `POST /api/automations/{id}/triggers/{triggerId}/rotate-secret` issues a new secret.
12. `owner` defaults to the caller's `name` claim (else `sub`) on create and may be edited.
    Cancelling a run (`POST /api/runs/{id}/cancel`) needs Operator. Automation reads need Viewer, every
    write Operator; workflow limits are Admin.
13. Parameters are validated with JsonSchema.Net (ADR-0016) against the stored schema of the version the
    automation selects (the pinned one, or the latest at save time).
14. The branch is `m4/automations-and-run-lifecycle`.

## Implementation notes (M4)

- **Schema** (migration `AutomationsAndRunLifecycle`, `Schema/0004_automations.sql`): `automations.deleted_at`
  with the partial unique name index; `automation_triggers.webhook_secret` (renamed from `webhook_secret_hash`);
  `data_protection_keys`; on `workflow_runs` the columns admission needs, fixed when the run is created —
  `limit_keys` (sorted; `automation:{id}` left out under Allow), `required_labels` (the automation's) and
  `run_timeout` — plus `cancel_reason` (`user`, `replaced`, `timeout`) and `blocked_on`; `task_runs.cancel_requested_at`.
- **Run creation** (`RunCreator`) locks the automation row, looks the idempotency key up first (so a retried
  delivery is a duplicate even after the automation was disabled), resolves "follow latest" to the highest
  version that is not retired, then applies the policy. A Skip has no run to own its `run.skipped` event, so the
  event is filed under the automation's id (`run_events.workflow_run_id = automation id`).
- **Trigger loop** (`CronTriggers`): one due trigger per transaction, taken with its automation row
  `FOR UPDATE OF t, a SKIP LOCKED`, so an Operator's edit (automation row, then trigger rows) never deadlocks with
  it — the trigger fires on the next pass. A trigger whose firing throws is excluded for the rest of the pass and
  logged; the others still fire.
- **Admission** (`RunAdmission`): the group's advisory lock is held on a side connection for the whole pass, and
  each candidate run gets its own transaction (run row `SKIP LOCKED`, then its limit rows in key order). Holding
  the rows of several runs in one transaction would lock keys out of order across runs. A missing limit row is
  unlimited and not held. When `group-runs` blocks a run, the rest of the group's candidates are passed over at
  once. `run.blocked` is written only when the limit a run waits for changes, not on every pass.
- **Releasing limits** (`LimitRows`, `RunFinalizer`): when an attempt ends, `group-tasks` and — if the run ended —
  every key in `held_limits` are locked together in key order and decremented, so the "lock in key order" rule
  of ADR-0009 holds on every path. A Pending run holds nothing, so cancelling it releases nothing.
- **Cancellation** (`RunCancellation`): run row, then tasks (Waiting/Queued/Retry wait ⇒ Cancelled in one
  statement, Leased/Running ⇒ `cancel_requested` in the next, so a task a concurrent claim just leased is still
  signalled), then the run is finalized at once when nothing is leased. The reaper closes a cancelled attempt
  30 s after the request (outcome `cancelled`, error `CancelGraceExpired`) and starts the cancellation of
  Running runs past `deadline_at` (reason `timeout` ⇒ the run ends `timed_out`).
- **Replace** cancels the oldest active run that is not already cancelling; the new run stays Pending until the
  replaced run has released the automation limit.
- **Run events** added: `run.created`, `run.skipped`, `run.blocked`, `run.admitted`, `run.cancelling`,
  `run.cancelled`, `run.timed_out`, `task.cancel_requested`, `task.cancelled`; a finished run's event carries the
  limits it released.
- **API**: deleting keeps the `automation:{id}` limit row, so the deleted automation's remaining runs stay under
  their limit. Enabling recomputes every cron trigger's next fire from `now()` (missed times do not fire); an
  update recomputes it only when the expression, zone or enabled state changes. A trigger's kind cannot change.
  The create response echoes the parameters as sent; reads return them as stored (`jsonb` key order).

## Risks / Trade-offs

- [Clock skew between replicas] → all comparisons use Postgres `now()`.
- [Replace policy cancels long runs unexpectedly] → editor shows a warning (M5); event logged.
- [Secrets pasted into parameters] → parameters reference secrets by name; OQ-07 decides the store.
