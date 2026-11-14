# Spec Delta

## MODIFIED Requirements

### Requirement: Role-aware actions (NFR-Security)
The console SHALL only offer actions the signed-in person's role permits.

#### Scenario: Viewer sees no mutating actions
- **WHEN** a Viewer opens a run detail page
- **THEN** no Cancel action is offered

#### Scenario: Operator can cancel
- **WHEN** an Operator opens a Running run's detail page
- **THEN** a Cancel action is offered

#### Scenario: Viewer cannot re-run
- **WHEN** a Viewer opens a Failed run's detail page
- **THEN** no Re-run failed tasks action is offered

## ADDED Requirements

### Requirement: Queue page (OBS-04)
The console SHALL show a queue page listing every host group with its queued tasks, oldest wait, pending runs, active leases, lease expirations in the last hour and paused state, refreshed at least every 10 seconds without reloading the page.

#### Scenario: Backlog visible
- **WHEN** a Viewer opens the queue page while group `etl` has 12 queued tasks, the oldest waiting 7 minutes
- **THEN** the `etl` row shows 12 queued and an oldest wait of 7 minutes

#### Scenario: Live refresh
- **WHEN** the queued count of a group changes while the queue page is open
- **THEN** the page shows the new count within 10 seconds

### Requirement: Re-run action (WF-10)
The run detail page SHALL offer Operators a Re-run failed tasks action on a Failed or TimedOut run, and SHALL show the run's re-run count. After a re-run the page SHALL show the run's new state without reloading.

#### Scenario: Operator re-runs
- **WHEN** an Operator opens a Failed run and chooses Re-run failed tasks
- **THEN** the page shows the run Pending with re-run count 1, and the action is no longer offered

#### Scenario: Succeeded run
- **WHEN** an Operator opens a Succeeded run
- **THEN** no Re-run failed tasks action is offered

### Requirement: Notification settings in the editor (OBS-05)
The automation editor SHALL let Operators add, change and remove notification channels: kind, events, and recipients or URL. A webhook channel's URL SHALL be write-only, and its signing secret SHALL be shown once, after the save that created or rotated it.

#### Scenario: Add a webhook channel
- **WHEN** an Operator adds a webhook channel on Failed and saves
- **THEN** the editor shows the channel's signing secret once, and on reopening shows the channel with the URL's host only

#### Scenario: Channel error shown
- **WHEN** an Operator saves an email channel without recipients
- **THEN** the editor shows the server's message next to that channel

### Requirement: Audit history view (AUT-11)
The console SHALL show the audit entries of an automation and of a host group, newest first, each with its actor, time, kind of change and the fields that changed between before and after.

#### Scenario: Automation history
- **WHEN** a Viewer opens the history of an automation whose priority was changed from 50 to 60
- **THEN** the newest entry shows the actor, the time, an update and priority 50 → 60

#### Scenario: Host group history
- **WHEN** a Viewer opens the history of host group `etl` after it was paused
- **THEN** the newest entry shows the pause with its actor and time
