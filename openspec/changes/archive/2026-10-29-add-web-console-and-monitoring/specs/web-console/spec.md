# Spec Delta

## Purpose

Provides the browser console through which people browse workflows, configure automations, follow
runs and logs, and see the state of hosts, with actions limited by their role.

## ADDED Requirements

### Requirement: Workflow catalog page (WF-09)
The console SHALL show every workflow definition with its DAG, its parameter schema and its version history.

#### Scenario: Browse versions
- **WHEN** a Viewer opens `nightly-sales-report` in the catalog
- **THEN** the latest version's DAG and parameters are shown and earlier versions can be selected

### Requirement: Automation editor (AUT-03)
The console SHALL let Operators create and edit automations, with a parameter form generated from the selected workflow's parameter schema and validation errors shown next to the failing fields.

#### Scenario: Generated form
- **WHEN** an Operator selects `nightly-sales-report` in the editor
- **THEN** a form with `region`, `reportDate` and `recipients` fields is shown, with `region` marked required

#### Scenario: Server rejects a value
- **WHEN** the server rejects a parameter value on save
- **THEN** the editor shows the server's message next to that field and keeps the other inputs

### Requirement: Runs list page (OBS-01)
The console SHALL list runs with filters for automation, workflow, status, host group and date range.

#### Scenario: Filter by status
- **WHEN** a Viewer filters the runs list to Failed
- **THEN** only Failed runs are listed

### Requirement: Run detail page (OBS-02)
The console SHALL show a run's DAG with each task labelled and coloured by its state, and per task its attempts, hosts, timings, output and error.

#### Scenario: Failed task highlighted
- **WHEN** a Viewer opens a Failed run
- **THEN** the failed task is shown in the failed state with its error, and skipped tasks are shown as skipped

### Requirement: Log viewer (OBS-03)
The console SHALL show a task attempt's logs and follow them live while the attempt runs.

#### Scenario: Live view
- **WHEN** a Viewer opens the logs of a running task
- **THEN** new lines appear without reloading the page

### Requirement: Hosts page (HST-06)
The console SHALL show every host with its status, slots used and total, current tasks and last heartbeat, and let Operators drain or undrain a host.

#### Scenario: Drain from the page
- **WHEN** an Operator drains a host from the hosts page
- **THEN** the host is shown as draining

### Requirement: Role-aware actions (NFR-Security)
The console SHALL only offer actions the signed-in person's role permits.

#### Scenario: Viewer sees no mutating actions
- **WHEN** a Viewer opens a run detail page
- **THEN** no Cancel action is offered

#### Scenario: Operator can cancel
- **WHEN** an Operator opens a Running run's detail page
- **THEN** a Cancel action is offered
