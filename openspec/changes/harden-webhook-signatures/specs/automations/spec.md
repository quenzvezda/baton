# Spec Delta

## MODIFIED Requirements

### Requirement: Triggers (AUT-04)
An automation SHALL have one or more triggers of kind cron (with an IANA time zone), manual Run now, or webhook verified by an HMAC-SHA256 signature, keyed with the trigger's secret, over a signed timestamp, the delivery id and the request body. A webhook request whose signed timestamp is more than 5 minutes from the server's time SHALL be rejected. A webhook trigger's secret SHALL be shown only when it is created or rotated and SHALL be stored encrypted.

#### Scenario: Cron in a time zone
- **WHEN** a trigger `0 2 * * *` in `Asia/Singapore` is enabled
- **THEN** a run is created at 02:00 Singapore time each day

#### Scenario: Run now
- **WHEN** an Operator clicks Run now on an enabled automation
- **THEN** one run is created immediately

#### Scenario: Run now on a disabled automation
- **WHEN** an Operator clicks Run now on a disabled automation
- **THEN** the request is rejected with 409 Conflict and no run is created

#### Scenario: Valid webhook
- **WHEN** a webhook request carries a correct signature for the trigger's secret, its delivery id and its body, with a current timestamp
- **THEN** one run is created and the response is 202 Accepted with the run id

#### Scenario: Invalid webhook signature
- **WHEN** a webhook request has a missing or wrong signature
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Stale webhook timestamp
- **WHEN** a correctly signed webhook request carries a timestamp 6 minutes before the server's time
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Replay with another delivery id
- **WHEN** a captured webhook request is resent with its signature but a different delivery id
- **THEN** the response is 401 Unauthorized and no run is created

#### Scenario: Webhook secret shown once
- **WHEN** an Operator creates an automation with a webhook trigger
- **THEN** the response contains the trigger's secret, and reading the automation later does not
