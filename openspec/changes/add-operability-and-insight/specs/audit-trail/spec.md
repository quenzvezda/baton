# Spec Delta

## Purpose

Records who changed the platform's configuration, when, and from what to what, so operators can audit
and explain every change to automations, host groups, agent credentials and workflow run limits.

## ADDED Requirements

### Requirement: Configuration changes are audited (AUT-11)
Every successful change to an automation, a host group, an agent credential or a workflow run limit SHALL be recorded as an audit entry with the acting person, the time, the kind of change, and the state before and after. The entry SHALL be stored if and only if the change is stored.

#### Scenario: Automation edited
- **WHEN** Operator `ana` changes the priority of automation `APAC nightly sales report` from 50 to 60
- **THEN** an audit entry for that automation shows `ana`, the time, an update, priority 50 before and 60 after

#### Scenario: Automation created
- **WHEN** an Operator creates an automation
- **THEN** an audit entry shows a create with no state before and the new automation after

#### Scenario: Automation deleted
- **WHEN** an Operator deletes an automation
- **THEN** an audit entry shows a delete with the automation before and no state after, and it stays readable after the deletion

#### Scenario: Group paused
- **WHEN** an Operator pauses dispatch for host group `etl`
- **THEN** an audit entry for `etl` shows a pause by that Operator

#### Scenario: Rejected change
- **WHEN** an Operator's update is rejected with 412 Precondition Failed
- **THEN** no audit entry is written

#### Scenario: Agent token revoked
- **WHEN** an Admin revokes an agent token of group `etl`
- **THEN** an audit entry shows the revocation by that Admin with the token's id and name, without the token

### Requirement: No secrets in the audit log (AUT-11)
An audit entry SHALL NOT contain a webhook secret, an agent token, a notification webhook URL or any other secret. Where a secret was issued, rotated or changed, the entry SHALL say so without its value.

#### Scenario: Webhook secret rotated
- **WHEN** an Operator rotates the secret of a webhook trigger
- **THEN** the audit entry records a secret rotation for that trigger and neither the old nor the new secret

#### Scenario: Notification URL changed
- **WHEN** an Operator replaces the URL of a webhook notification channel
- **THEN** the audit entry records that the URL changed and shows neither URL in full

### Requirement: Audit log query (AUT-11)
Viewers SHALL be able to read the audit entries of one entity, or of one entity type, newest first, cursor-paged, filtered by time range.

#### Scenario: History of an automation
- **WHEN** a Viewer reads the audit entries of an automation that was created and then edited twice
- **THEN** three entries are returned, newest first, each with its actor, time, kind of change, before and after

### Requirement: Audit retention (AUT-11)
Audit entries SHALL be kept for 1 year and then deleted by daily maintenance on one replica, independently of the 90-day run history retention.

#### Scenario: Old entry removed
- **WHEN** maintenance runs and an audit entry is 366 days old
- **THEN** it is deleted, and an entry 364 days old is kept

#### Scenario: Outlives run history
- **WHEN** an automation was changed 120 days ago
- **THEN** its audit entry is still readable although runs from that time have been purged
