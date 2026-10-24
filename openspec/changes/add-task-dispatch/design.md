# Design

## Context

See proposal.md. The SQL shapes are in design spec §7 "Hot-path queries" (Claim, Heartbeat, Complete,
Expired leases). Constraints: ADR-0003 (one transaction per transition), ADR-0006 (fencing),
ADR-0007 (hand-written SQL), ADR-0008 (SKIP LOCKED loops), ADR-0009 (group-tasks counter).

## Goals / Non-Goals

**Goals:** p95 < 2 s Queued→Leased with a free slot; no task leased to two hosts at once; a dead host's
work recovered in about one lease period; 50 claims/s sustained on one group in the integration rig.

**Non-Goals:** run admission and triggers (M4); aging (M6); preemption (out of scope for v1).

## Decisions

- **Claim transaction.** (1) lock `group-tasks:{id}` row if the group has a task limit and compute the
  budget = min(free slots, room); (2) `SELECT … FOR UPDATE SKIP LOCKED` ordered by
  `effective_priority DESC, queued_at` with eligibility predicates; (3) update to Leased with
  `nextval('lease_token_seq')`; (4) increment `in_use`; insert `task_attempts` rows; commit.
  Host status/draining/heartbeat-age and group are read from the host row in the same transaction.
- **Long-poll.** The gRPC handler waits on an in-process signal fed by a single `LISTEN tasks_ready`
  connection per replica (payload = group id), re-checking every 2 s regardless; returns early on the
  first non-empty claim or at `max_wait`.
- **Complete.** Fenced `UPDATE … WHERE lease_token = @token` returning 0 rows ⇒ `FAILED_PRECONDITION`.
  Downstream keys come from the cached definition graph (by hash). Rows reaching `upstream_remaining = 0`
  are evaluated with the domain trigger-rule function; skips propagate iteratively in the same
  transaction. Then release `group-tasks`, `NOTIFY tasks_ready, '<group>'`. Run finalization is a hook
  completed in M4.
- **Start.** Leased → Running fenced by token; sets `started_at`. Optional for correctness, used for timing.
- **Retry classification.** Failed + retryable + attempts left ⇒ Retry wait with `not_before = now() + backoff`;
  timeout counts as retryable; non-retryable or exhausted ⇒ Failed.
- **Reaper (5 s).** Expired leases ⇒ attempt `lost`; `cancel_requested` ⇒ Cancelled (no retry); otherwise
  retry classification. Also fails tasks running past `timeout + 60 s` as a backstop.
- **Agent.** Claims only free slots; per-task `CancelAfter(timeout)`; heartbeat reply's
  `lost_task_ids` cancels the handler and suppresses reporting; `cancel_task_ids` cancels and reports Cancelled.
  Logs are buffered and streamed with `AppendLogs`, fenced by token.

## Risks / Trade-offs

- [Hot `group-tasks` row serializes claims] → only when a task limit is set; measure; batch claims.
- [NOTIFY storms] → one notify per completing transaction per group; claims coalesce.
- [Long GC pauses exceed lease] → 60 s lease vs 10 s heartbeat; per-group tuning (OQ-02).
- [Skip propagation over large DAGs in one transaction] → bounded by DAG size; test with 500-task DAG.

## Open Questions

- Default long-poll and lease values per group are tunable; final defaults confirmed at Gate G1 load test.
