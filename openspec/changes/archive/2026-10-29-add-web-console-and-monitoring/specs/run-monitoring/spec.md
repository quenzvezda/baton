# Spec Delta

## Purpose

Gives users and tools visibility into workflow runs: finding runs, inspecting every task and attempt,
and reading handler logs both live and after the fact.

## ADDED Requirements

### Requirement: Runs query (OBS-01)
The control plane SHALL list runs newest first, filterable by automation, workflow, status, host group and creation date range, with cursor-based paging.

#### Scenario: Filter failed runs of one automation
- **WHEN** a Viewer lists runs filtered by automation A and status Failed for the last 7 days
- **THEN** only Failed runs of A created in that range are returned, newest first, with a cursor when more exist

#### Scenario: Next page
- **WHEN** a Viewer requests the next page with the returned cursor
- **THEN** the following runs are returned without duplicates or gaps

### Requirement: Run detail (OBS-02)
The control plane SHALL return a run with its definition version, status, timings and every task with its state, attempts, hosts, timings, output and error.

#### Scenario: Retried task
- **WHEN** a Viewer opens a run whose `build-report` failed once and then succeeded
- **THEN** the detail shows two attempts for `build-report` with their hosts, times, the first error and the final output

### Requirement: Handler log capture (OBS-03)
Log lines a handler writes SHALL be stored per task attempt in the order written, with time and level, and SHALL be readable after the attempt ends for as long as the run is retained.

#### Scenario: Logs after completion
- **WHEN** a Viewer reads the logs of attempt 1 of a finished task
- **THEN** all lines written by that attempt are returned in order with time and level

### Requirement: Live log tail (OBS-03)
While an attempt is running, a client SHALL be able to follow its logs live and receive new lines within 2 seconds of the agent sending them.

#### Scenario: Follow a running task
- **WHEN** a Viewer follows the logs of a running attempt and the handler writes a line
- **THEN** the line appears in the stream within 2 seconds
