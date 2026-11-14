# Spec Delta

## MODIFIED Requirements

### Requirement: Role-based authorization (NFR-Security)
Every control-plane operation SHALL require one of three roles: Viewer may read, including queue statistics and the audit log; Operator may additionally create and change automations and their notification channels, run, re-run, cancel, drain and pause; Admin may additionally manage host groups, agent credentials and definitions.

#### Scenario: Viewer reads
- **WHEN** a Viewer lists runs
- **THEN** the request succeeds

#### Scenario: Viewer cannot cancel
- **WHEN** a Viewer cancels a run
- **THEN** the response is 403 Forbidden and the run is unchanged

#### Scenario: Operator cannot manage host groups
- **WHEN** an Operator updates a host group's limits
- **THEN** the response is 403 Forbidden

#### Scenario: Admin manages host groups
- **WHEN** an Admin updates a host group's limits
- **THEN** the request succeeds

#### Scenario: Viewer cannot re-run
- **WHEN** a Viewer re-runs a Failed run
- **THEN** the response is 403 Forbidden and the run is unchanged

#### Scenario: Viewer reads the audit log
- **WHEN** a Viewer reads the audit entries of a host group
- **THEN** the request succeeds
