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
- **Webhooks.** `X-Signature: sha256=<hex HMAC of raw body>`; secret stored hashed and shown once at creation;
  constant-time compare; body becomes parameter overrides only if the automation allows it (default: no).
- **Optimistic concurrency.** `row_version` exposed as ETag; `If-Match` mismatch ⇒ 412.

## Risks / Trade-offs

- [Clock skew between replicas] → all comparisons use Postgres `now()`.
- [Replace policy cancels long runs unexpectedly] → editor shows a warning (M5); event logged.
- [Secrets pasted into parameters] → parameters reference secrets by name; OQ-07 decides the store.
