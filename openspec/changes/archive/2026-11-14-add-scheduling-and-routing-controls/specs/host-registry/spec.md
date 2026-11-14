# Spec Delta

## MODIFIED Requirements

### Requirement: Draining hosts (HST-04)
Operators SHALL be able to drain a host so that it finishes its running tasks and takes no new ones, and to undrain it. A draining host SHALL still take the remaining tasks of runs already pinned to it, so that those runs can finish.

#### Scenario: Drain with running tasks
- **WHEN** an Operator drains host `etl-01` while it runs 2 tasks
- **THEN** the 2 tasks continue to completion and the host receives no new tasks

#### Scenario: Undrain
- **WHEN** an Operator undrains a draining host
- **THEN** the host becomes Online and eligible for new tasks

#### Scenario: Drain survives an outage
- **WHEN** a drained host goes Offline and later heartbeats again
- **THEN** it is Draining, not Online, until an Operator undrains it

#### Scenario: Viewer cannot drain
- **WHEN** a Viewer drains a host
- **THEN** the response is 403 Forbidden and the host is unchanged

#### Scenario: Drain with a pinned run
- **WHEN** an Operator drains host `etl-01` while a run pinned to it still has Waiting tasks
- **THEN** those tasks are dispatched to `etl-01` when they become Queued, and no task of any other run is

## ADDED Requirements

### Requirement: Dispatch pause (ORC-13)
Operators SHALL be able to pause and resume dispatch for a host group. While paused, no task of the group SHALL be leased; leased and running tasks continue, runs are still admitted and their tasks queue. On resume, waiting hosts SHALL be offered work without waiting for their next poll. Pausing a paused group, or resuming a running one, SHALL succeed without change.

#### Scenario: Pause stops new leases
- **WHEN** an Operator pauses group `etl` while 3 of its tasks run and 5 are Queued
- **THEN** the 3 tasks run to completion and the 5 stay Queued while the group is paused

#### Scenario: Runs still admitted while paused
- **WHEN** a run for group `etl` is created while `etl` is paused and its limits have room
- **THEN** the run is admitted and Running, and its root tasks are Queued

#### Scenario: Resume
- **WHEN** an Operator resumes group `etl` while an agent of `etl` is long-polling with free slots
- **THEN** the agent receives queued tasks within 2 seconds

#### Scenario: Paused state is visible
- **WHEN** a Viewer lists host groups while `etl` is paused
- **THEN** `etl` is returned as paused

#### Scenario: Viewer cannot pause
- **WHEN** a Viewer pauses a host group
- **THEN** the response is 403 Forbidden and the group is unchanged
