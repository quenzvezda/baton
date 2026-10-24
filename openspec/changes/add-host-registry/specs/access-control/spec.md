# Spec Delta

## Purpose

Controls who can use the control plane: people sign in through the organization's identity provider
and act within one of three roles, so that reading, operating and administering are separated.

## ADDED Requirements

### Requirement: Single sign-on (NFR-Security)
People SHALL authenticate to the control plane and web console through the organization's OpenID Connect identity provider; unauthenticated requests to protected endpoints SHALL be rejected.

#### Scenario: Anonymous request
- **WHEN** a request without a valid session or token calls `GET /api/automations`
- **THEN** the response is 401 Unauthorized

### Requirement: Role-based authorization (NFR-Security)
Every control-plane operation SHALL require one of three roles: Viewer may read; Operator may additionally create and change automations, run, cancel, drain and pause; Admin may additionally manage host groups, agent credentials and definitions.

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
