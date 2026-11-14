# Spec Delta

## MODIFIED Requirements

### Requirement: Host eligibility (ORC-04)
A queued task SHALL be dispatched only to a host that is Online and not draining, belongs to the run's host group whose dispatch is not paused, carries every label required by the automation and the task, runs a build containing the task's definition version, and has a free slot. A task of a PinnedRun run SHALL go only to the host the run is pinned to, which may be draining.

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
- **WHEN** a draining host claims and no run is pinned to it
- **THEN** it receives no task

#### Scenario: No free slots
- **WHEN** a host with 4 slots holds 4 leased tasks
- **THEN** it receives no further task until one finishes

#### Scenario: Paused group
- **WHEN** an Online host of group `etl` claims while dispatch for `etl` is paused
- **THEN** it receives no task, and the queued tasks stay Queued

#### Scenario: Task of a run pinned elsewhere
- **WHEN** host `etl-02` claims and the only queued task belongs to a run pinned to `etl-01`
- **THEN** `etl-02` receives no task

## ADDED Requirements

### Requirement: Priority aging (ORC-06)
While a task is Queued its effective priority SHALL be its base priority plus one point per aging step waited (default 5 minutes), capped at the aging cap (default 20) and at 100, recomputed at least every 30 seconds. Step and cap are platform settings. A task that is queued again SHALL start from its base priority.

#### Scenario: Waiting task ages
- **WHEN** a task with base priority 40 has been Queued for 12 minutes with default settings
- **THEN** its effective priority is 42

#### Scenario: Cap
- **WHEN** a task with base priority 40 has been Queued for 3 hours with default settings
- **THEN** its effective priority is 60

#### Scenario: Ceiling
- **WHEN** a task with base priority 95 has been Queued for 1 hour with default settings
- **THEN** its effective priority is 100

#### Scenario: Aged task overtakes
- **WHEN** a task with base priority 40 has waited 2 hours and a task with base priority 55 was just queued in the same group, with one free slot
- **THEN** the aged task, at effective priority 60, is dispatched first

#### Scenario: Retry starts again from base
- **WHEN** a task that aged to effective priority 60 from base 40 fails and is queued again for its next attempt
- **THEN** its effective priority is 40 when it is queued again

### Requirement: Run pinning (ORC-12)
The first claim that leases a task of an unpinned PinnedRun run SHALL pin the run to the claiming host, which SHALL carry every label required by any task of the run; at most one host SHALL ever be pinned to a run, even when several hosts claim its tasks at the same moment.

#### Scenario: First claim pins
- **WHEN** host `etl-01` claims the first root task of a PinnedRun run
- **THEN** the run is pinned to `etl-01` and every later task of the run is dispatched only to `etl-01`

#### Scenario: Concurrent claims of parallel roots
- **WHEN** a PinnedRun run has two root tasks and hosts `etl-01` and `etl-02` claim at the same moment
- **THEN** both tasks are leased to the same host, and the other host receives neither

#### Scenario: Pinned host must fit the whole run
- **WHEN** a PinnedRun run's second task requires label `large-memory` and host `etl-02` without it claims the run's first task
- **THEN** `etl-02` receives no task of that run, and a host with `large-memory` may pin it

#### Scenario: Retry on the pinned host
- **WHEN** a task of a run pinned to `etl-01` fails with attempts remaining
- **THEN** its next attempt is dispatched only to `etl-01`

### Requirement: Pinned host lost (ORC-12)
A Running PinnedRun run SHALL fail with the reason "pinned host lost" when its pinned host is Offline or moved to another group: its tasks not yet final SHALL be cancelled as on run cancellation, and the run SHALL end Failed rather than Cancelled.

#### Scenario: Pinned host goes offline
- **WHEN** the host a running run is pinned to sends no heartbeat for 30 seconds
- **THEN** cancellation of the run begins, its queued and waiting tasks are cancelled, and once every task is final the run is Failed with reason "pinned host lost"

#### Scenario: Pinned host moved
- **WHEN** an Admin moves the host a running run is pinned to into another group
- **THEN** the run fails with reason "pinned host lost"

#### Scenario: Pinned host restarts in time
- **WHEN** the pinned host's agent restarts and heartbeats again within 30 seconds
- **THEN** the run is not failed, and its remaining tasks still go only to that host
