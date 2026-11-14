# Spec Delta

## ADDED Requirements

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
