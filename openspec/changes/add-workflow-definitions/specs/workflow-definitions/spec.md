# Spec Delta

## Purpose

Defines how workflows are declared in code as versioned DAGs of tasks, validated and registered at
startup, exposed through the catalog, and what data each task receives and returns.

## ADDED Requirements

### Requirement: Code-defined workflows (WF-01)
The system SHALL load workflow definitions only from compiled code shipped with the platform; it SHALL NOT accept workflow definitions created or edited at runtime in v1.

#### Scenario: Definitions are discovered from the definitions package
- **WHEN** the control plane starts with a definitions package containing two workflow definitions
- **THEN** both definitions are registered and listed in the catalog

### Requirement: Workflows are directed acyclic graphs (WF-02)
A workflow definition SHALL be a directed acyclic graph of tasks with explicit dependencies, and tasks whose dependencies are all satisfied SHALL be eligible to run in parallel.

#### Scenario: Independent branches are eligible together
- **WHEN** a workflow has tasks `extract-orders` and `extract-customers` with no dependencies and `build-report` depending on both
- **THEN** both extract tasks become eligible to run at the same time
- **AND** `build-report` becomes eligible only after both have finished

### Requirement: Startup validation (WF-03)
The control plane SHALL validate every definition at startup and SHALL refuse to start if any definition is invalid, reporting the workflow key and the reason.

#### Scenario: Cycle stops startup
- **WHEN** a definition contains tasks `a` → `b` → `a`
- **THEN** the control plane fails to start with an error naming the workflow and the tasks in the cycle

#### Scenario: Invalid task key stops startup
- **WHEN** a definition contains a duplicate task key, a key that is not kebab-case, or a key longer than 64 characters
- **THEN** the control plane fails to start with an error naming the offending key

#### Scenario: Unresolvable handler stops startup
- **WHEN** a task's handler is not registered or expects a parameter type different from the workflow's
- **THEN** the control plane fails to start with an error naming the task and handler

#### Scenario: Unserializable parameters stop startup
- **WHEN** a workflow's parameter type cannot be serialized to JSON or cannot produce a JSON Schema
- **THEN** the control plane fails to start with an error naming the workflow

#### Scenario: Out-of-bounds options stop startup
- **WHEN** a task declares more than 10 attempts or a timeout longer than 24 hours
- **THEN** the control plane fails to start with an error naming the task and the bound

#### Scenario: Graph without a root stops startup
- **WHEN** a definition has no task without dependencies
- **THEN** the control plane fails to start

### Requirement: Immutable versions (WF-04)
Each definition SHALL be identified by `key@version` and a hash of its canonical graph; a registered `key@version` SHALL NOT change, and every run SHALL record the exact definition version it executes.

#### Scenario: Changed graph under the same version is rejected
- **WHEN** `nightly-sales-report@2` is already registered and the control plane starts with a different graph for `nightly-sales-report@2`
- **THEN** the control plane fails to start with an error asking to bump the version

#### Scenario: Re-registering an unchanged definition is a no-op
- **WHEN** the control plane restarts with an identical `nightly-sales-report@2`
- **THEN** startup succeeds and the catalog still holds exactly one `nightly-sales-report@2`

#### Scenario: Old versions remain available
- **WHEN** `nightly-sales-report@3` is registered while runs of version 2 exist
- **THEN** version 2 remains in the catalog and its runs keep referencing version 2

### Requirement: Per-task execution options (WF-05)
Each task SHALL support retry attempts and backoff, a timeout, a trigger rule, required host labels and a priority offset; unspecified options SHALL default to 3 attempts, 30 s initial delay, multiplier 2, maximum delay 10 minutes, a 1 hour timeout and trigger rule AllSuccess.

#### Scenario: Defaults apply
- **WHEN** a task declares no options
- **THEN** its registered graph shows 3 attempts, 30 s initial delay, multiplier 2, 10 minute maximum delay, a 1 hour timeout and AllSuccess

#### Scenario: Declared options are registered
- **WHEN** a task declares 4 attempts, a 15 minute timeout and the label `large-memory`
- **THEN** the registered graph contains exactly those values for that task

### Requirement: Trigger rules (WF-06)
When all upstream tasks of a task are final, the task SHALL run or be skipped by its trigger rule: AllSuccess runs only if every upstream task succeeded; AllDone always runs; OneFailed runs only if at least one upstream task failed.

#### Scenario: AllSuccess with a skipped parent
- **WHEN** an AllSuccess task's upstream contains a Skipped task
- **THEN** the task is Skipped

#### Scenario: AllDone after a failure
- **WHEN** an AllDone task's upstream contains a Failed task
- **THEN** the task runs

#### Scenario: OneFailed when everything succeeded
- **WHEN** every upstream task of a OneFailed task Succeeded
- **THEN** the task is Skipped

### Requirement: Task inputs and outputs (WF-07)
A task SHALL receive the run's parameters and the outputs of its ancestor tasks, and SHALL return a JSON output of at most 256 KB.

#### Scenario: Ancestor outputs are available
- **WHEN** `build-report` depends on `extract-orders` which succeeded with an output
- **THEN** `build-report` can read that output

#### Scenario: Oversized output fails the attempt
- **WHEN** a task returns an output larger than 256 KB
- **THEN** the attempt fails with an output-too-large error and is not retried

### Requirement: Ancestor-only output access (WF-07)
Reading the output of a task that is not an ancestor SHALL fail at run time, because that task is not guaranteed to have run.

#### Scenario: Reading a sibling's output
- **WHEN** a task reads the output of a task that is not among its ancestors
- **THEN** the attempt fails with an error naming both tasks

### Requirement: Non-retryable failures (WF-08)
A task SHALL be able to signal a non-retryable failure, which SHALL fail the task immediately without consuming its remaining attempts.

#### Scenario: Non-retryable failure on first attempt
- **WHEN** a task with 4 attempts signals a non-retryable failure on attempt 1
- **THEN** the task is Failed after 1 attempt and no retry is scheduled

### Requirement: Stable idempotency key (NFR-ExecutionGuarantee)
Every attempt SHALL receive an idempotency key formed from the run id and task key that is identical across all attempts of that task in that run.

#### Scenario: Retry sees the same key
- **WHEN** attempt 1 of `build-report` in run R fails and attempt 2 starts
- **THEN** both attempts receive the idempotency key `R:build-report`

### Requirement: Parameter schema (AUT-03)
Each registered definition SHALL expose a JSON Schema for its parameters, derived from the parameter type, for validating and rendering automation parameters.

#### Scenario: Schema reflects the parameter type
- **WHEN** a workflow's parameters have a required string `Region` and an optional date `ReportDate`
- **THEN** the registered schema marks `region` required as a string and `reportDate` optional as a date

### Requirement: Catalog API (WF-09)
The control plane SHALL list the latest version of every registered definition and SHALL return, for any registered `key@version`, its task graph, parameter schema and the list of versions for that key.

#### Scenario: List latest versions
- **WHEN** a Viewer requests the workflow list and versions 1 and 2 of `nightly-sales-report` exist
- **THEN** the list contains `nightly-sales-report` with version 2 as latest

#### Scenario: Unknown version
- **WHEN** a Viewer requests `nightly-sales-report@9` which does not exist
- **THEN** the response is 404 Not Found
