# Spec Delta

## ADDED Requirements

### Requirement: Fire time preview in the editor (AUT-10)
The automation editor SHALL show, under each cron trigger, its next 5 fire times in the trigger's time zone, refreshed while the expression or zone is edited; an invalid expression or zone SHALL show the server's message next to that field instead.

#### Scenario: Live preview
- **WHEN** an Operator types `0 2 * * *` with zone `Asia/Singapore` in a cron trigger
- **THEN** the next 5 fire times are shown as 02:00 Singapore time on the next 5 days

#### Scenario: Invalid expression
- **WHEN** an Operator types `61 * * * *`
- **THEN** no fire times are shown and the cron field shows the validation message

### Requirement: Scheduling and placement fields (AUT-08, ORC-12)
The automation editor SHALL let Operators choose each cron trigger's misfire policy, with a catch-up limit shown only for CatchUp, and the automation's placement mode.

#### Scenario: Catch-up limit only for CatchUp
- **WHEN** an Operator switches a cron trigger's misfire policy from FireOnce to CatchUp
- **THEN** a catch-up limit field appears with 3 filled in

#### Scenario: Placement saved
- **WHEN** an Operator selects PinnedRun and saves
- **THEN** reopening the automation shows PinnedRun

### Requirement: Group pause on the hosts page (ORC-13)
The hosts page SHALL show each host group's dispatch state, marking paused groups, and SHALL offer Operators and Admins Pause and Resume actions per group.

#### Scenario: Pause from the page
- **WHEN** an Operator pauses group `etl` from the hosts page
- **THEN** `etl` is shown as paused and a Resume action is offered

#### Scenario: Viewer sees the state only
- **WHEN** a Viewer opens the hosts page while `etl` is paused
- **THEN** `etl` is shown as paused and no Pause or Resume action is offered
