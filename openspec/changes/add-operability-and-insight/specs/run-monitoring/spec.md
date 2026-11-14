# Spec Delta

## MODIFIED Requirements

### Requirement: Run detail (OBS-02)
The control plane SHALL return a run with its definition version, status, timings, re-run count and every task with its state, attempts, hosts, timings, output and error.

#### Scenario: Retried task
- **WHEN** a Viewer opens a run whose `build-report` failed once and then succeeded
- **THEN** the detail shows two attempts for `build-report` with their hosts, times, the first error and the final output

#### Scenario: Re-run run
- **WHEN** a Viewer opens a run that was re-run once and then succeeded
- **THEN** the detail shows re-run count 1 and, for the task that ran again, the attempts of both the first run and the re-run

## ADDED Requirements

### Requirement: Queue statistics (OBS-04)
The control plane SHALL return, for every host group: its queued tasks, the wait of its oldest queued task, its pending runs, its active leases (tasks Leased or Running), its lease expirations in the last 60 minutes, and whether dispatch is paused. Figures SHALL be computed from current state with database time.

#### Scenario: Backlog
- **WHEN** group `etl` has 12 Queued tasks, the oldest queued 7 minutes ago, and 3 Running tasks
- **THEN** its statistics show 12 queued, an oldest wait of about 7 minutes and 3 active leases

#### Scenario: Lease expirations
- **WHEN** two attempts in group `etl` were lost to lease expiry 10 and 50 minutes ago, and one 70 minutes ago
- **THEN** its statistics show 2 lease expirations in the last hour

#### Scenario: Idle group
- **WHEN** a group has no queued, pending or running work
- **THEN** it is listed with zeros and no oldest wait
