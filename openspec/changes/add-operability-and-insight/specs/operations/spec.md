# Spec Delta

## ADDED Requirements

### Requirement: Telemetry export (OBS-06)
The control plane API, the orchestrator, every agent and the web console SHALL export traces, metrics and logs through the OpenTelemetry protocol when an export endpoint is configured, and SHALL export nothing and need no collector when none is. Each process SHALL identify itself by service name, version and instance.

#### Scenario: Agent exports
- **WHEN** an agent starts with an OpenTelemetry export endpoint configured and runs a task
- **THEN** the collector receives traces, metrics and logs from service `agent` with that agent's host name as instance

#### Scenario: No endpoint
- **WHEN** a process starts without an export endpoint
- **THEN** it runs normally and attempts no export

### Requirement: Trace per run (OBS-06)
Each handler attempt SHALL be recorded as a span in the trace of the run it belongs to, with the run id, task key, attempt, host and outcome, and marked as an error when the attempt fails or times out. Calls between agents and the orchestrator SHALL carry trace context.

#### Scenario: Two tasks, one trace
- **WHEN** a run created by Run now executes two tasks on two agents
- **THEN** both handler spans belong to the trace that the Run now request started, each with its task key and attempt

#### Scenario: Failed attempt
- **WHEN** a handler attempt throws
- **THEN** its span has error status and the exception's type and message

### Requirement: Handler logs in telemetry (OBS-06)
Log lines a handler writes SHALL also be exported as telemetry logs, carrying the run id, task key and attempt and correlated with the attempt's span, in addition to being stored for the console.

#### Scenario: Log correlated
- **WHEN** a handler writes a warning during attempt 2 of `build-report`
- **THEN** the exported log record has warning level, the run id, `build-report`, attempt 2 and the trace and span of that attempt

### Requirement: Queue and lease metrics (OBS-04, OBS-06)
The orchestrator SHALL publish, per host group, the number of queued tasks, the wait of the oldest queued task and the number of active leases, refreshed at least every 30 seconds. It SHALL count every lease that expires, per host group, so an alert can fire on queue age and on lease expirations.

#### Scenario: Queue age metric
- **WHEN** the oldest queued task of group `etl` has waited 7 minutes
- **THEN** within 30 seconds the oldest-wait metric for `etl` reports about 420 seconds

#### Scenario: Lease expiry counted
- **WHEN** the reaper finds an expired lease of a task in group `etl`
- **THEN** the lease-expiration metric for `etl` increases by 1

### Requirement: Notification metrics (OBS-05, OBS-06)
The orchestrator SHALL count notification deliveries that succeed and those that fail after their last attempt, by channel kind.

#### Scenario: Failed delivery counted
- **WHEN** an email delivery fails its last attempt
- **THEN** the failed-notification metric for kind `email` increases by 1
