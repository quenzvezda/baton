# Spec Delta

## Purpose

Keeps a Baton deployment healthy over time: run history is retained for 90 days and then removed,
missing future history partitions are reported before they break writes, and corrections made by the
limit safety net are observable.

## ADDED Requirements

### Requirement: History retention (NFR-Retention)
The orchestrator SHALL, once a day on one replica, remove run history older than 90 days: monthly history partitions whose entire month ended more than 90 days ago SHALL be dropped, and runs that finished more than 90 days ago SHALL be deleted together with their tasks. Runs that are not finished SHALL never be deleted, whatever their age.

#### Scenario: Old month dropped
- **WHEN** maintenance runs and a history partition covers a month that ended more than 90 days ago
- **THEN** that partition is dropped and newer partitions are kept

#### Scenario: Partly expired month kept
- **WHEN** a history partition's month ended less than 90 days ago, even though some of its rows are older than 90 days
- **THEN** the partition is kept until its whole month is past the retention window

#### Scenario: Old finished run purged
- **WHEN** maintenance runs and a run finished 91 days ago
- **THEN** the run and its tasks are deleted, and a run that finished 89 days ago is kept

#### Scenario: Old active run kept
- **WHEN** a run created 100 days ago is still Running
- **THEN** maintenance does not delete it or its tasks

### Requirement: Partition health (NFR-Retention)
The orchestrator health endpoint SHALL report degraded, while staying available, when any history table has fewer than 2 monthly partitions beyond the current month.

#### Scenario: Maintenance stalled
- **WHEN** a history table has partitions only up to next month
- **THEN** the health endpoint reports degraded and names the partition check

#### Scenario: Partitions ahead
- **WHEN** every history table has partitions 3 months ahead
- **THEN** the partition check is healthy

### Requirement: Limit correction signal (NFR-Auditability)
Every correction the limit reconciler makes SHALL be logged as a warning and counted in a metric labelled with the kind of limit corrected, so an alert can fire on any correction.

#### Scenario: Drifted counter
- **WHEN** a limit counter says 3 in use but live runs and tasks hold 1
- **THEN** the counter is set to 1, a warning is logged, and the correction metric increases by 1 for that limit kind
