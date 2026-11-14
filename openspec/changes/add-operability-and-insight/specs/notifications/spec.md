# Spec Delta

## Purpose

Tells people when a run of their automation fails or times out, by email or by a signed webhook set per
automation, delivered at least once without affecting the run's outcome.

## ADDED Requirements

### Requirement: Notification channels per automation (OBS-05)
An automation SHALL have zero or more notification channels, each either email (1 to 20 recipient addresses) or webhook (one HTTPS URL), and each set to fire on Failed, TimedOut or both. Channels SHALL be created, changed and removed with the automation.

#### Scenario: Email channel saved
- **WHEN** an Operator saves an automation with an email channel to `sales-leads@example.com` on Failed
- **THEN** the automation is returned with that channel

#### Scenario: Invalid channel
- **WHEN** an Operator saves a webhook channel with an `http://` URL, or an email channel with no recipient or no event
- **THEN** the save is rejected with 400 and a message for that channel's field

### Requirement: Webhook channel secrets (OBS-05)
A webhook channel's URL and signing secret SHALL be stored encrypted and never returned by a read. The signing secret SHALL be returned once, when the channel is created or its secret rotated. A read SHALL show the URL's scheme and host only, and an update that omits the URL SHALL keep the stored one.

#### Scenario: Read hides the URL
- **WHEN** a Viewer reads an automation with a webhook channel to `https://hooks.example.com/services/T0/B0/abc`
- **THEN** the channel shows `https://hooks.example.com` and no path, query or secret

#### Scenario: Update keeps the URL
- **WHEN** an Operator changes a webhook channel's events without sending its URL
- **THEN** the stored URL and secret are unchanged

### Requirement: Notification on failure or timeout (OBS-05)
When a run of an automation ends Failed or TimedOut, one delivery SHALL be scheduled for each of the automation's channels that fires on that outcome, in the same step that ends the run. Runs that end Succeeded or Cancelled SHALL notify no channel.

#### Scenario: Failed run
- **WHEN** a run of an automation with an email channel on Failed and a webhook channel on TimedOut ends Failed
- **THEN** one email delivery is scheduled and no webhook delivery

#### Scenario: Timed out run
- **WHEN** a run of an automation with a channel on both outcomes ends TimedOut
- **THEN** one delivery is scheduled for that channel

#### Scenario: Re-run fails again
- **WHEN** a re-run of a Failed run ends Failed again
- **THEN** a new delivery is scheduled for each channel on Failed

### Requirement: Notification content (OBS-05)
A notification SHALL identify the automation, the run, its workflow key and version, its outcome, its start and finish times, and each failed task with its error message. When a console address is configured it SHALL include a link to the run.

#### Scenario: Email body
- **WHEN** a run fails because task `build-report` failed with `Timeout talking to warehouse`
- **THEN** the email names the automation and run, says Failed, and lists `build-report` with that message

### Requirement: Signed webhook deliveries (OBS-05)
A webhook notification SHALL be an HTTP POST of a JSON document. It SHALL carry a delivery id that is the same on every retry of that delivery, and a signature over the timestamp, the delivery id and the body made with the channel's signing secret, in the same format Baton verifies on inbound webhooks.

#### Scenario: Receiver verifies
- **WHEN** a receiver recomputes the signature of a webhook notification with the channel's secret
- **THEN** it matches, and a retry of the same delivery carries the same delivery id

### Requirement: At-least-once delivery with retries (OBS-05)
A delivery SHALL be attempted until it succeeds, up to 5 attempts, waiting about 1, 5, 15 and 30 minutes between attempts. A webhook succeeds on a 2xx response within 10 seconds. An email succeeds when the mail server accepts it. Redirects SHALL NOT be followed.

#### Scenario: Receiver down then up
- **WHEN** a webhook receiver answers 503 to the first attempt and 200 to the second
- **THEN** the delivery is sent twice, about one minute apart, and is recorded as sent

#### Scenario: Two replicas
- **WHEN** two orchestrator replicas run while a delivery is due
- **THEN** the delivery is attempted by one replica only

### Requirement: Failed notifications do not fail the run (OBS-05, OQ-08)
A delivery that has not succeeded after its last attempt SHALL be recorded as failed in the run's event timeline with its last error, and counted in a metric. The run's status SHALL NOT change because of a notification.

#### Scenario: Receiver never answers
- **WHEN** all 5 attempts of a delivery for a Failed run time out
- **THEN** the run stays Failed, its timeline shows the notification failed with the last error, and the failed-notification metric increases by 1

#### Scenario: Sent notification recorded
- **WHEN** a delivery succeeds
- **THEN** the run's timeline shows the notification sent, with the channel and attempt count
