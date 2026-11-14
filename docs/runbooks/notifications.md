# Run notifications are not arriving

**Symptom:** a run of an automation with a notification channel ended Failed or TimedOut, and nobody got
the email or the webhook. Or the `baton.notifications.failed` metric increased.

Notifications (OBS-05) go through an outbox in Postgres ([ADR-0018](../adr/0018-run-notifications-through-a-postgres-outbox.md)):
- The step that ends the run writes one delivery per matching channel.
- The orchestrator's `NotificationLoop` (every 5 s, on every replica) sends it.
- A failed attempt is tried again after about 1, 5, 15 and 30 minutes, and the 5th failure gives the
  delivery up.

A notification never changes the run's status (OQ-08).

## Configuration

Set on the **orchestrator**, which sends. The API also reads the `Baton:Notifications` webhook rules when
a channel is saved, so set those on both.

| Setting | Where | Meaning |
| --- | --- | --- |
| `Baton__Notifications__Smtp__Host`, `__Port`, `__Security` (`StartTls`, `SslOnConnect`, `Auto`, `None`), `__From` | orchestrator | Mail relay ([ADR-0019](../adr/0019-email-delivery-with-mailkit.md)). Without `Host`, every email delivery fails at once with "Email delivery is not configured". `From` is required when `Host` is set, and startup fails otherwise. |
| `Baton__Notifications__Smtp__UserName`, `__Password` | orchestrator, from the environment or a secret store | Relay credentials. Never in the repository. |
| `Baton__Notifications__AllowedWebhookHosts__0`, `__1`, … | API and orchestrator | Optional allowlist of webhook hosts. Empty means any HTTPS host. Set it to keep Operators from pointing webhooks at internal services. |
| `Baton__Notifications__AllowInsecureWebhooks` | development only | Accepts `http://` webhook URLs. Never in production. |
| `Baton__Notifications__ConsoleBaseUrl` | orchestrator | Notifications link to `<ConsoleBaseUrl>/runs/<id>`. |
| `Baton__DataProtection__CertificatePath`, `__CertificatePassword` | **API and orchestrator, the same certificate** | The orchestrator decrypts webhook URLs and signing secrets with the key ring the API wrote. A different or missing certificate makes every webhook delivery fail with "cannot be decrypted". |

Locally, `docker compose up -d` starts Mailpit, and `src/Baton.Orchestrator/appsettings.Development.json`
points SMTP at it. Read the mail at http://localhost:8025.

## Checks

1. **Was a delivery scheduled?** Only Failed and TimedOut runs notify, only for channels set to that
   outcome, and never for a deleted automation.

   ```sql
   SELECT d.id, c.kind, d.event, d.status, d.attempts, d.next_attempt_at, d.last_error, d.sent_at
   FROM notification_deliveries d JOIN notification_channels c ON c.id = d.channel_id
   WHERE d.workflow_run_id = '<runId>' ORDER BY d.id;
   ```
   - No row: check the automation's channels and their `on` list in the console's Notifications section.
   - `pending` with a future `next_attempt_at`: it is waiting for a retry, and `last_error` says why.
   - `failed`: given up. `last_error` holds the last reason, and the run timeline shows `notification.failed`.
2. **Is a loop running?** Every orchestrator replica runs it. A `NotificationLoop tick failed` error in the
   orchestrator log means ticks roll back. A Postgres outage stops it until Postgres is back
   ([postgres-outage](postgres-outage.md)).
3. **Read the error.**
   - `The receiver answered 4xx`: the receiver rejects the delivery, for example a wrong secret or a deleted
     hook. It is not retried.
   - `5xx`, a timeout or `could not be reached`: retried.
   - `cannot be decrypted`: the key ring settings (above).
   - `The mail server refused`: a 5xx SMTP reply, not retried. `could not take the message`: retried.

## Receivers

A webhook is a `POST` of JSON with `X-Delivery-Id: ntf_<n>` and `X-Signature: t=<unix>,v1=<hex>`. The
signature is an HMAC-SHA256 with the channel's signing secret over `<t>.<delivery id>.<raw body>`, the same
scheme as Baton's inbound webhooks (see README "Run an automation"). Receivers must:
- answer 2xx within 10 s;
- deduplicate on `X-Delivery-Id`, because delivery is at least once;
- reject timestamps more than 5 minutes away.

Redirects are not followed. A lost signing secret is replaced with
`POST /api/automations/<id>/notifications/<channelId>/rotate-secret`, which shows the new secret once.

## Confirm

- The delivery is `sent`, the run timeline shows `notification.sent` with the channel and attempt count,
  and `baton.notifications.sent` increased for that kind.
- After fixing a cause, deliveries that already gave up are not sent again. Re-running the run sends new
  ones if it fails again ([rerun-failed-runs](rerun-failed-runs.md)).
