# Tasks

## 1. Domain

- [ ] 1.1 Pure DAG-advance function: given completed task + outcome, return downstream transitions incl. skip propagation (ORC-03); verify `DagAdvanceTests` incl. 500-task DAG
- [ ] 1.2 Retry classification + `Backoff.NextDelay` (ORC-10); verify `BackoffTests` and `RetryClassificationTests` (timeout retryable, non-retryable, exhausted)

## 2. Queue SQL

- [ ] 2.1 Claim query with eligibility, budget and ordering (ORC-04, ORC-05, HST-01); verify integration tests per eligibility scenario and ordering ties
- [ ] 2.2 Concurrency test: 8 parallel claimers, 1,000 tasks ⇒ each task leased exactly once; verify `ClaimRaceTests`
- [ ] 2.3 Heartbeat renewal returning lost/cancel lists (ORC-07); verify renewal and wrong-token tests
- [ ] 2.4 Fenced Start/Complete/AppendLogs (ORC-08); verify stale-token test returns FAILED_PRECONDITION and changes nothing
- [ ] 2.5 Complete + DAG advance + group-tasks release + NOTIFY in one transaction (ORC-03); verify integration test that a crash before commit leaves no partial state
- [ ] 2.6 Attempt rows and run events per transition (NFR-Auditability); verify history test "lost then succeeded"

## 3. Orchestrator

- [ ] 3.1 `AgentService.Claim` long-poll with LISTEN-fed wake-up and 2 s re-check (NFR-DispatchLatency); verify latency test p95 < 2 s with 1 waiting agent
- [ ] 3.2 `ReaperLoop` (5 s) for expired leases and timeout backstop (ORC-09, WF-05); verify host-dies and unresponsive-handler integration tests
- [ ] 3.3 `RetryPromoterLoop` (1 s) Retry wait → Queued (ORC-10); verify due-retry test
- [ ] 3.4 Test helper that seeds a Running run with task runs (until M4 admission exists); verify used by the tests above

## 4. Agent

- [ ] 4.1 `AgentWorker` claim loop limited to free slots, heartbeat loop handling lost/cancel lists; verify unit tests with a fake `AgentClient`
- [ ] 4.2 `TaskExecutor` with per-task timeout, non-retryable handling, 256 KB output check, always-report semantics; verify unit tests per outcome
- [ ] 4.3 `ILogger` capture to buffered `AppendLogs` stream; verify log lines arrive in order with sequence numbers

## 5. End-to-end

- [ ] 5.1 Chaos test: kill an agent mid-task ⇒ task retried on another agent and run completes; verify in `Baton.Integration.Tests`
- [ ] 5.2 Throughput smoke: 50 claims/s for 60 s on one group without errors; verify test report attached to PR

## Workflow follow-up

- Archive after M3 exit criteria are met.
