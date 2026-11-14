# Spec Delta

## ADDED Requirements

### Requirement: Re-run a failed run (WF-10)
Operators SHALL be able to re-run a run that ended Failed or TimedOut. The run SHALL keep its id, parameters, definition version and routing, return to Pending, count one more re-run, and record who requested it in its timeline. A re-run of a run in any other state SHALL be rejected with 409 Conflict.

#### Scenario: Re-run a failed run
- **WHEN** an Operator re-runs a Failed run
- **THEN** the run is Pending with re-run count 1, and its timeline shows the re-run request with the Operator

#### Scenario: Re-run a timed-out run
- **WHEN** an Operator re-runs a TimedOut run
- **THEN** the run is Pending and its earlier timeout no longer applies

#### Scenario: Not re-runnable
- **WHEN** an Operator re-runs a run that is Running, Pending, Succeeded or Cancelled
- **THEN** the response is 409 Conflict and the run is unchanged

#### Scenario: Newer definition version
- **WHEN** a Failed run of `nightly-sales-report@3` is re-run after version 4 was registered
- **THEN** the re-run uses version 3

### Requirement: Re-run goes through admission (WF-10)
A re-run run SHALL be admitted like a new Pending run: under the same limits, priority order and run timeout, with its deadline counted from its new start. Until it is admitted none of its tasks SHALL be dispatched.

#### Scenario: Run limit full
- **WHEN** a run is re-run while its automation already has its maximum number of Running runs
- **THEN** it stays Pending until a slot frees up and then starts

### Requirement: Re-run reuses successful outputs (WF-10)
When a re-run run is admitted, tasks that Succeeded SHALL keep their state and output and SHALL NOT run again. Every Failed or Cancelled task SHALL run again, as SHALL every Skipped task downstream of one of them; any other Skipped task SHALL stay Skipped. Tasks run again in dependency order and see the kept outputs of their upstream tasks.

#### Scenario: Last task failed
- **WHEN** a run of `extract-a`, `extract-b` → `build-report` → `publish` failed in `build-report`, `publish` was Skipped, and the run is re-run and admitted
- **THEN** `build-report` is Queued with the outputs of both extracts as inputs, `publish` waits for it, and the extracts are not run again

#### Scenario: Failure-only task
- **WHEN** a failed run's `notify-failure` task, which runs only when an upstream task failed, Succeeded and the run is re-run
- **THEN** `notify-failure` is not run again

#### Scenario: Re-run succeeds
- **WHEN** every task that runs again in a re-run succeeds
- **THEN** the run ends Succeeded and its limits are released

#### Scenario: Re-run fails again
- **WHEN** a task fails again in a re-run
- **THEN** the run ends Failed and can be re-run again

### Requirement: Re-run attempts (WF-10)
A task that runs again SHALL continue its attempt numbers, so earlier attempts and their logs stay readable. It SHALL get its full retry budget again, and SHALL keep the same idempotency key as before.

#### Scenario: Attempts continue
- **WHEN** a task with up to 3 attempts failed after attempt 3 and its run is re-run
- **THEN** its next attempt is attempt 4, it may retry up to attempt 6, and attempts 1 to 3 are still shown with their hosts, errors and logs

#### Scenario: Same idempotency key
- **WHEN** task `build-report` of run R runs again in a re-run
- **THEN** its handler sees idempotency key `R:build-report`, the same as in its earlier attempts
