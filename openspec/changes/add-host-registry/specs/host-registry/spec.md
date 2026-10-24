# Spec Delta

## Purpose

Manages host groups and their limits, and keeps an authoritative, live inventory of the agents that
execute tasks: who they are, where they belong, what they can run and whether they are available.

## ADDED Requirements

### Requirement: Host group administration (HST-01)
Admins SHALL be able to create, edit and delete host groups with a unique name, an optional maximum number of concurrent runs and an optional maximum number of concurrent tasks; an absent limit SHALL mean unlimited.

#### Scenario: Create a group with limits
- **WHEN** an Admin creates group `etl` with max 5 concurrent runs and max 20 concurrent tasks
- **THEN** the group exists with those limits and they are enforced from the next admission and claim

#### Scenario: Duplicate name
- **WHEN** an Admin creates a group named like an existing group
- **THEN** the request is rejected with 409 Conflict

#### Scenario: Delete a group in use
- **WHEN** an Admin deletes a group that still has hosts or automations
- **THEN** the request is rejected and the group is unchanged

#### Scenario: Non-admin cannot manage groups
- **WHEN** an Operator tries to create a host group
- **THEN** the request is rejected with 403 Forbidden

### Requirement: Agent self-registration (HST-02)
An agent SHALL register on start with its name, group, labels, slot capacity, agent version and the definition hashes its build contains; registering again with the same name SHALL update that host rather than create a new one.

#### Scenario: First registration
- **WHEN** agent `etl-01` registers into group `etl` with 4 slots and labels `large-memory`
- **THEN** the host appears Online in group `etl` with 4 slots, that label, its version and its definition hashes

#### Scenario: Re-registration after upgrade
- **WHEN** agent `etl-01` restarts with a new build and registers again
- **THEN** the same host is updated with the new version and definition hashes

#### Scenario: Unknown group
- **WHEN** an agent registers into a group that does not exist
- **THEN** registration is rejected and the agent retries with backoff

### Requirement: Agent authentication (NFR-Security)
Every agent call SHALL be authenticated with a credential bound to one host group; calls without a valid credential, or for a different group, SHALL be rejected.

#### Scenario: Missing credential
- **WHEN** an agent calls the agent API without a credential
- **THEN** the call is rejected as unauthenticated and nothing is recorded

#### Scenario: Credential for another group
- **WHEN** an agent with a credential for group `etl` registers into group `gpu`
- **THEN** the call is rejected as permission denied

### Requirement: Heartbeat liveness (HST-03)
Agents SHALL heartbeat every 10 seconds, and a host with no heartbeat for 30 seconds SHALL be treated as Offline and receive no new work.

#### Scenario: Missed heartbeats
- **WHEN** host `etl-01` sends no heartbeat for 30 seconds
- **THEN** it is shown as Offline and no task is dispatched to it

#### Scenario: Recovery
- **WHEN** an Offline host heartbeats again
- **THEN** it returns to Online

### Requirement: Draining hosts (HST-04)
Operators SHALL be able to drain a host so that it finishes its running tasks and takes no new ones, and to undrain it.

#### Scenario: Drain with running tasks
- **WHEN** an Operator drains host `etl-01` while it runs 2 tasks
- **THEN** the 2 tasks continue to completion and the host receives no new tasks

#### Scenario: Undrain
- **WHEN** an Operator undrains a draining host
- **THEN** the host becomes Online and eligible for new tasks

### Requirement: Single group membership (HST-05)
Each host SHALL belong to exactly one host group, and moving a host to another group SHALL take effect on the host's next claim.

#### Scenario: Move between groups
- **WHEN** an Admin moves host `etl-01` from `etl` to `gpu`
- **THEN** its next claim only receives tasks routed to `gpu`
- **AND** tasks it already holds are unaffected

### Requirement: Host inventory (HST-06)
The control plane SHALL return every host with its group, status, slots used and total, current tasks and last heartbeat time.

#### Scenario: Inventory reflects running work
- **WHEN** a Viewer lists hosts while `etl-01` runs 3 of its 4 slots
- **THEN** `etl-01` is listed as Online with 3 of 4 slots used, its 3 current tasks and its last heartbeat time
