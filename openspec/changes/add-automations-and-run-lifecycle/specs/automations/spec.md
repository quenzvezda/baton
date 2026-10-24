# Spec Delta

## Purpose

Lets users configure automations: which workflow runs, with what parameters, on which hosts, at what
priority, how often, and what happens when a trigger fires while earlier runs are still active.

## ADDED Requirements

### Requirement: Automation management (AUT-01)
Operators SHALL be able to create, edit, clone, enable, disable and delete an automation with a unique name, a description and an owner; deleting an automation SHALL NOT stop its active runs.

#### Scenario: Create
- **WHEN** an Operator creates automation `APAC nightly sales report` with valid settings
- **THEN** it is stored, enabled and returned with its triggers

#### Scenario: Disable stops triggers
- **WHEN** an Operator disables an automation with a cron trigger
- **THEN** no further runs are created by its triggers until it is enabled again

#### Scenario: Clone
- **WHEN** an Operator clones an automation
- **THEN** a new disabled automation with the same settings and a new name is created

#### Scenario: Delete with active run
- **WHEN** an Operator deletes an automation that has a running run
- **THEN** the automation is removed and the run continues to completion

### Requirement: Concurrent edit protection (AUT-01)
Updating an automation SHALL require the version last read by the client and SHALL be rejected if the automation changed since.

#### Scenario: Stale update
- **WHEN** two Operators load the same automation and both save changes
- **THEN** the second save is rejected with 412 Precondition Failed and the first change is kept

### Requirement: Workflow selection (AUT-02)
An automation SHALL select one workflow definition and either pin a version or follow the latest version; when following latest, each run SHALL use the latest version registered at the time the run is created.

#### Scenario: Follow latest
- **WHEN** an automation follows the latest version and version 3 is registered
- **THEN** the next run created uses version 3 and earlier runs keep their versions

#### Scenario: Pinned version
- **WHEN** an automation pins version 2 and version 3 is registered
- **THEN** new runs keep using version 2

### Requirement: Parameter validation (AUT-03)
Workflow parameters of an automation SHALL be validated against the selected definition's parameter schema on save, and invalid values SHALL be rejected with the failing fields named.

#### Scenario: Missing required field
- **WHEN** an Operator saves an automation whose parameters omit the required `region`
- **THEN** the save is rejected with a validation error naming `region`

### Requirement: Triggers (AUT-04)
An automation SHALL have one or more triggers of kind cron (with an IANA time zone), manual Run now, or webhook verified by an HMAC-SHA256 signature of the request body.

#### Scenario: Cron in a time zone
- **WHEN** a trigger `0 2 * * *` in `Asia/Singapore` is enabled
- **THEN** a run is created at 02:00 Singapore time each day

#### Scenario: Run now
- **WHEN** an Operator clicks Run now on an enabled automation
- **THEN** one run is created immediately

#### Scenario: Valid webhook
- **WHEN** a webhook request carries a correct signature for the trigger's secret
- **THEN** one run is created and the response is 202 Accepted with the run id

#### Scenario: Invalid webhook signature
- **WHEN** a webhook request has a missing or wrong signature
- **THEN** the response is 401 Unauthorized and no run is created

### Requirement: Cron accuracy (NFR-TriggerAccuracy)
A cron trigger's run SHALL be created within 5 seconds of its scheduled fire time while the platform is up.

#### Scenario: On-time fire
- **WHEN** a cron trigger is due at 02:00:00
- **THEN** its run exists by 02:00:05

### Requirement: Routing (AUT-05)
An automation SHALL name exactly one target host group and an optional set of host labels that every task of its runs requires in addition to the task's own labels.

#### Scenario: Automation labels apply to all tasks
- **WHEN** an automation requires label `region-apac`
- **THEN** none of its tasks is dispatched to a host without `region-apac`

### Requirement: Priority and run limit (AUT-06)
An automation SHALL have a priority from 0 to 100 (default 50) and a maximum number of concurrent runs (default 1).

#### Scenario: Out-of-range priority
- **WHEN** an Operator saves an automation with priority 120
- **THEN** the save is rejected

### Requirement: Concurrency policy (AUT-07)
When a trigger fires while an automation is at its maximum concurrent runs, the automation's policy SHALL decide: Skip creates no run and records the skipped fire; Queue creates a run that waits; Replace cancels the oldest active run and creates a new one; Allow creates a run ignoring the automation limit while still respecting workflow and group limits.

#### Scenario: Skip
- **WHEN** a Skip automation with max 1 run fires while a run is active
- **THEN** no run is created and a skipped-fire event is recorded

#### Scenario: Queue
- **WHEN** a Queue automation with max 1 run fires while a run is active
- **THEN** a Pending run is created and starts after the active run finishes

#### Scenario: Replace
- **WHEN** a Replace automation with max 1 run fires while a run is active
- **THEN** the active run is cancelled and a new run is created

#### Scenario: Allow
- **WHEN** an Allow automation with max 1 run fires while a run is active
- **THEN** a second run is created and admitted if workflow and group limits have room
