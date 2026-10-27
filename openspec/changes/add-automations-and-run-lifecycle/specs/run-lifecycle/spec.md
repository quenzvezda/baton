# Spec Delta

## Purpose

Governs a workflow run from creation to its final status: exactly-once creation per trigger firing,
admission under limits, final status from task outcomes, deadlines and user cancellation.

## ADDED Requirements

### Requirement: One run per firing (ORC-01)
Each trigger firing SHALL create at most one workflow run, identified by the automation, the trigger and the fire time, regardless of how many orchestrator replicas process it or how often a webhook is retried with the same delivery.

#### Scenario: Two replicas see the same due trigger
- **WHEN** two orchestrator replicas process the same due cron trigger
- **THEN** exactly one run exists for that fire time

#### Scenario: Webhook delivery retried
- **WHEN** a webhook sender retries a signed request with the same `X-Delivery-Id`
- **THEN** no second run is created and the response names the run created by the first request

### Requirement: Admission under limits (ORC-02)
A new run SHALL stay Pending until every applicable limit (automation, workflow, host group runs) has room, and SHALL then start with all those limits reserved; Pending runs SHALL be considered in priority order, and a run that does not fit SHALL NOT block lower-priority runs that do fit.

#### Scenario: Group full
- **WHEN** group `etl` allows 5 concurrent runs and 5 are running
- **THEN** a new run routed to `etl` stays Pending until one of them finishes

#### Scenario: Pass over
- **WHEN** the highest-priority Pending run is blocked by its workflow limit and a lower-priority run in the same group fits every limit
- **THEN** the lower-priority run starts

### Requirement: Workflow run limit (ORC-02)
Admins SHALL be able to set, change and remove a maximum number of concurrent runs for a workflow key, applied to runs of every version of that workflow; without one, the workflow's runs are unlimited.

#### Scenario: Workflow limit reached
- **WHEN** workflow `nightly-sales-report` is limited to 1 concurrent run and one of its runs is Running
- **THEN** another run of it stays Pending until the first finishes, whichever automation created it

### Requirement: Run start (ORC-02)
When a run is admitted it SHALL become Running and record its start time, its exact definition version and its deadline, and its task runs SHALL be created with root tasks Queued and all others Waiting.

#### Scenario: Admitted run
- **WHEN** a run of a workflow with 2 roots and 3 dependent tasks is admitted
- **THEN** the run is Running with 2 Queued and 3 Waiting tasks

### Requirement: Run outcome
When every task of a run is final, the run SHALL become Failed if any task Failed and Succeeded otherwise, Skipped tasks alone SHALL NOT fail a run, and all limits the run holds SHALL be released.

#### Scenario: Only skips
- **WHEN** all tasks of a run Succeeded except `notify-failure`, which was Skipped
- **THEN** the run is Succeeded

#### Scenario: One failure
- **WHEN** one task of a run Failed and the rest Succeeded or were Skipped
- **THEN** the run is Failed and its limits are released

### Requirement: Run timeout (AUT-09)
An automation SHALL support an optional run timeout; a run still active after its start time plus that timeout SHALL be cancelled and end as TimedOut.

#### Scenario: Deadline passed
- **WHEN** a run with a 2 hour timeout is still Running 2 hours after it started
- **THEN** cancellation begins and the run ends as TimedOut

### Requirement: Run cancellation (ORC-11)
Operators SHALL be able to cancel a run: its Waiting, Queued and Retry wait tasks SHALL be cancelled at once, its Leased and Running tasks SHALL receive a cancellation signal with a 30 second grace period, cancelled tasks SHALL NOT be retried, and the run SHALL end Cancelled once every task is final.

#### Scenario: Cancel with running task
- **WHEN** an Operator cancels a run with one Running and two Queued tasks
- **THEN** the run is Cancelling, the two Queued tasks are Cancelled, and the running task's host is told to stop on its next heartbeat

#### Scenario: Host does not respond
- **WHEN** the host running a cancelled task neither reports nor heartbeats
- **THEN** the task becomes Cancelled when its lease expires and is not retried, and the run ends Cancelled

#### Scenario: Handler ignores cancellation
- **WHEN** the host running a cancelled task keeps heartbeating but its handler has not stopped 30 seconds after the cancellation
- **THEN** the task becomes Cancelled, is not retried, and the host is told on its next heartbeat that it no longer holds the lease

#### Scenario: Cancel a finished run
- **WHEN** an Operator cancels a run that already Succeeded
- **THEN** the request is rejected with 409 Conflict

### Requirement: Run event timeline (NFR-Auditability)
Every run state transition, skipped fire and admission decision SHALL be recorded as a timestamped run event.

#### Scenario: Timeline of a queued run
- **WHEN** a Queue-policy run waits in Pending and is later admitted and succeeds
- **THEN** its timeline shows created, admitted, started and succeeded events in order with times
