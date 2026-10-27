# Spec Delta

## Purpose

Decides when each task of a running workflow is ready, which eligible host executes it and in what
order, and keeps every assignment safe through fenced, renewable leases with retries and recovery.

## ADDED Requirements

### Requirement: Task readiness (ORC-03)
A task SHALL become Queued when all its upstream tasks are final and its trigger rule holds, and SHALL become Skipped when all upstream tasks are final and its trigger rule does not hold; a Skipped task SHALL be treated as not succeeded by its downstream tasks.

#### Scenario: Last parent completes
- **WHEN** the last of `build-report`'s upstream tasks succeeds
- **THEN** `build-report` becomes Queued in the same step that records the parent's success

#### Scenario: Skip propagates
- **WHEN** `build-report` is Skipped and `publish-report` depends on it with AllSuccess
- **THEN** `publish-report` is Skipped too

#### Scenario: Root tasks
- **WHEN** a run starts
- **THEN** its tasks without dependencies are Queued and all others are Waiting

### Requirement: Host eligibility (ORC-04)
A queued task SHALL be dispatched only to a host that is Online and not draining, belongs to the run's host group, carries every label required by the automation and the task, runs a build containing the task's definition version, and has a free slot.

#### Scenario: Missing label
- **WHEN** a task requires label `large-memory` and the only claiming host lacks it
- **THEN** the task stays Queued

#### Scenario: Missing definition version
- **WHEN** a task belongs to definition hash H and the claiming host's build does not contain H
- **THEN** the task stays Queued

#### Scenario: Other group
- **WHEN** a host in group `gpu` claims while the only queued task is routed to `etl`
- **THEN** the host receives no task

#### Scenario: Draining host
- **WHEN** a draining host claims
- **THEN** it receives no task

#### Scenario: No free slots
- **WHEN** a host with 4 slots holds 4 leased tasks
- **THEN** it receives no further task until one finishes

### Requirement: Group task limit (HST-01)
The number of leased and running tasks in a host group SHALL never exceed the group's maximum concurrent tasks when one is set.

#### Scenario: Limit reached
- **WHEN** group `etl` has max 20 concurrent tasks and 20 are leased or running
- **THEN** no further task in `etl` is dispatched until one finishes

### Requirement: Dispatch order (ORC-05)
Within a host group, eligible queued tasks SHALL be dispatched by effective priority, highest first, then by queue time, oldest first; a task's base priority SHALL be its run's priority plus the task's offset, clamped to 0–100.

#### Scenario: Higher priority first
- **WHEN** tasks with effective priority 70 and 40 are both eligible for one free slot
- **THEN** the priority 70 task is dispatched

#### Scenario: Ties by age
- **WHEN** two eligible tasks have equal effective priority
- **THEN** the one queued earlier is dispatched first

#### Scenario: Clamping
- **WHEN** a run with priority 95 has a task with offset +10
- **THEN** the task's base priority is 100

### Requirement: Fenced leases (ORC-07)
A dispatched task SHALL be held under a lease that expires after the group's lease duration (default 60 seconds) unless renewed by the host's heartbeat, and every claim SHALL issue a new fencing token greater than any previously issued.

#### Scenario: Renewal
- **WHEN** a host heartbeats every 10 seconds while running a task for 5 minutes
- **THEN** the lease never expires and the task is not reassigned

#### Scenario: Heartbeat cadence follows the lease
- **WHEN** a host registers in a group whose lease duration is 30 seconds
- **THEN** it is told to heartbeat every 5 seconds, and every 10 seconds for leases of 60 seconds or more

#### Scenario: New token per attempt
- **WHEN** a task is claimed for attempt 2 after attempt 1 was lost
- **THEN** attempt 2's token is greater than attempt 1's

### Requirement: Fenced results (ORC-08)
Results, start signals and log lines from a host whose lease has expired or been replaced SHALL be rejected without changing any state, and the host SHALL be told the result was stale.

#### Scenario: Expired but not yet reaped
- **WHEN** a host's lease expired a moment ago and the reaper has not closed the attempt yet
- **THEN** the host's heartbeat reports the task as lost and its result is rejected as stale

#### Scenario: Late result after reassignment
- **WHEN** host A's lease on a task expired, host B claimed it, and host A then reports success
- **THEN** host A's result is rejected as stale and the task's state reflects only host B's attempt

### Requirement: Lost leases (ORC-09)
When a lease expires, the attempt SHALL be recorded as Lost and the task SHALL go to Retry wait if attempts remain, otherwise to Failed; a lost task whose cancellation was requested SHALL become Cancelled and not be retried.

#### Scenario: Host dies mid-task
- **WHEN** a host stops heartbeating while running attempt 1 of 3
- **THEN** within about one lease period plus 5 seconds attempt 1 is Lost and the task is in Retry wait

#### Scenario: Last attempt lost
- **WHEN** the lease of the final attempt expires
- **THEN** the task is Failed

### Requirement: Retry backoff (ORC-10)
A retryable failure SHALL wait before the next attempt for min(max delay, initial delay × multiplier^(n−1)) increased by a random jitter of 0–20 %, where n is the number of the failed attempt; a timeout SHALL count as retryable.

#### Scenario: Second retry delay
- **WHEN** attempt 2 of a task with defaults (30 s, ×2, max 10 min) fails
- **THEN** attempt 3 becomes due between 60 and 72 seconds later

#### Scenario: Timeout is retried
- **WHEN** attempt 1 exceeds the task timeout and attempts remain
- **THEN** the attempt is recorded as timed out and the task goes to Retry wait

### Requirement: Task timeout enforcement (WF-05)
A running attempt SHALL be cancelled when it exceeds its task timeout, and the platform SHALL fail any attempt still running 60 seconds past its timeout even if the host never reports.

#### Scenario: Unresponsive handler
- **WHEN** a handler ignores cancellation and its host keeps heartbeating 60 seconds past the timeout
- **THEN** the attempt is closed as timed out by the platform and its later result is rejected as stale

### Requirement: Run outcome
When every task of a running workflow run is final, the run SHALL end in the same step: Failed if any task Failed, otherwise Succeeded; Skipped tasks alone SHALL never fail a run.

#### Scenario: All tasks succeeded
- **WHEN** the last task of a running run succeeds and every other task succeeded or was skipped
- **THEN** the run is Succeeded with its finish time set

#### Scenario: A task failed
- **WHEN** the last task of a running run becomes final and one task of the run failed
- **THEN** the run is Failed

#### Scenario: Concurrent last tasks
- **WHEN** the last two running tasks of a run complete at the same moment on different hosts
- **THEN** the run ends exactly once

### Requirement: Limit counters stay correct (HST-01)
The platform SHALL periodically recompute every concurrency limit's usage from the live runs and tasks and correct any counter that drifted.

#### Scenario: Drifted counter
- **WHEN** the group task counter says 5 while 2 tasks of the group are leased or running
- **THEN** within one reconciliation period the counter says 2

### Requirement: Attempt history (NFR-Auditability)
Every attempt SHALL be recorded with its host, fencing token, start and finish times, outcome (succeeded, failed, timed out, lost, cancelled) and error, and every task state transition SHALL be recorded as a run event.

#### Scenario: Lost then succeeded
- **WHEN** attempt 1 is lost on host A and attempt 2 succeeds on host B
- **THEN** the task's history shows attempt 1 on A as lost and attempt 2 on B as succeeded, with their tokens and times

### Requirement: Dispatch latency (NFR-DispatchLatency)
When an eligible host is waiting for work with a free slot, a task that becomes Queued SHALL be leased to it within 2 seconds at the 95th percentile.

#### Scenario: Waiting agent is woken
- **WHEN** an agent is long-polling with a free slot and a task in its group becomes Queued
- **THEN** the agent receives the lease without waiting for its long-poll to time out
