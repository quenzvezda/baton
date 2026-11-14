# automations Specification

## Purpose
Lets users configure automations: which workflow runs, with what parameters, on which hosts, at what
priority, how often, and what happens when a trigger fires while earlier runs are still active.

## Requirements

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

#### Scenario: Name of a deleted automation
- **WHEN** an Operator deletes automation `APAC nightly sales report` and creates a new one with that name
- **THEN** the new automation is stored and the deleted one's runs still refer to the deleted automation

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
An automation SHALL have one or more triggers of kind cron (with an IANA time zone), manual Run now, or webhook verified by an HMAC-SHA256 signature, keyed with the trigger's secret, over a signed timestamp, the delivery id and the request body. A webhook request whose signed timestamp is more than 5 minutes from the server's time SHALL be rejected. A webhook trigger's secret SHALL be shown only when it is created or rotated and SHALL be stored encrypted.

#### Scenario: Cron in a time zone
- **WHEN** a trigger `0 2 * * *` in `Asia/Singapore` is enabled
- **THEN** a run is created at 02:00 Singapore time each day

#### Scenario: Run now
- **WHEN** an Operator clicks Run now on an enabled automation
- **THEN** one run is created immediately

#### Scenario: Run now on a disabled automation
- **WHEN** an Operator clicks Run now on a disabled automation
- **THEN** the request is rejected with 409 Conflict and no run is created

#### Scenario: Valid webhook
- **WHEN** a webhook request carries a correct signature for the trigger's secret, its delivery id and its body, with a current timestamp
- **THEN** one run is created and the response is 202 Accepted with the run id

#### Scenario: Invalid webhook signature
- **WHEN** a webhook request has a missing or wrong signature
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Stale webhook timestamp
- **WHEN** a correctly signed webhook request carries a timestamp 6 minutes before the server's time
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Replay with another delivery id
- **WHEN** a captured webhook request is resent with its signature but a different delivery id
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Webhook secret shown once
- **WHEN** an Operator creates an automation with a webhook trigger
- **THEN** the response contains the trigger's secret, and reading the automation later does not

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

### Requirement: Cron misfire policy (AUT-08)
Each cron trigger SHALL have a misfire policy: FireOnce (default), Skip or CatchUp with a limit from 1 to 10 (default 3). A fire time more than 60 seconds late is missed. FireOnce creates one run for the most recent missed time, Skip none, CatchUp runs for the most recent missed times up to the limit, oldest first. Fire times not yet missed always fire, and the missed times passed over SHALL be recorded with their count and range.

#### Scenario: FireOnce after an outage
- **WHEN** an hourly trigger with FireOnce missed 01:00, 02:00 and 03:00 and the platform is back at 03:30
- **THEN** one run is created for 03:00, 01:00 and 02:00 are recorded as missed, and the next fire is 04:00

#### Scenario: Skip after an outage
- **WHEN** a daily 02:00 trigger with Skip comes back at 02:05 after an outage
- **THEN** no run is created for 02:00, the missed time is recorded, and the next fire is 02:00 the next day

#### Scenario: CatchUp with a limit
- **WHEN** an hourly trigger with CatchUp limit 3 missed 5 fire times
- **THEN** runs are created for the 3 most recent missed times in time order and the 2 oldest are recorded as missed

#### Scenario: Slightly late fire is not a misfire
- **WHEN** a trigger with Skip is due at 02:00:00 and the platform reaches it at 02:00:20
- **THEN** a run is created for 02:00 as usual

#### Scenario: Catch-up runs follow the concurrency policy
- **WHEN** a CatchUp trigger creates 3 runs for an automation with the Skip concurrency policy and a limit of 1 run
- **THEN** the first run is created and the other 2 firings are skipped by the concurrency policy

#### Scenario: Catch-up limit out of range
- **WHEN** an Operator saves a CatchUp trigger with limit 0 or 11
- **THEN** the save is rejected with a validation error naming the catch-up limit

### Requirement: Fire time preview (AUT-10)
The control plane SHALL return, for a cron expression and time zone, the next 5 fire times after the current time, each as an instant and as local time in that zone; an invalid expression or zone SHALL be rejected with the same validation errors as saving a trigger.

#### Scenario: Preview in a time zone
- **WHEN** a Viewer previews `0 2 * * *` in `Asia/Singapore`
- **THEN** the next 5 days' 02:00 Singapore times are returned, each with its UTC instant

#### Scenario: Expression that stops firing
- **WHEN** an expression has fewer than 5 future fire times
- **THEN** only the remaining fire times are returned

#### Scenario: Invalid expression
- **WHEN** the expression is `61 * * * *`
- **THEN** the request is rejected with a validation error naming the cron expression

### Requirement: Placement mode (ORC-12)
An automation SHALL have a placement mode: PerTask (default), where each task may run on any eligible host of the group, or PinnedRun, where every task of a run runs on the same host. The mode SHALL be fixed for a run when the run is created.

#### Scenario: Default placement
- **WHEN** an Operator creates an automation without a placement mode
- **THEN** the automation's placement mode is PerTask

#### Scenario: Change does not affect active runs
- **WHEN** an Operator switches an automation from PerTask to PinnedRun while one of its runs is Running
- **THEN** that run keeps dispatching PerTask, and runs created afterwards are PinnedRun
