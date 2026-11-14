# Proposal

## Why

**Gate G1 — v1 production readiness**, Security criterion; G1 security review finding M2
(`docs/reports/g1-security-review.md`). Not a roadmap milestone. A webhook signature today covers only the
request body: the delivery id and the time are not signed, so one captured request replays forever with a
fresh `X-Delivery-Id`, each replay creating a run, until the secret is rotated. Requirement: AUT-04.

## What Changes

- **BREAKING (webhook contract).** `X-Signature` becomes `t=<unix seconds>,v1=<hex>`: HMAC-SHA256, keyed
  with the trigger's secret, of `<t>.<X-Delivery-Id>.<raw body>`. The old `sha256=<hex of body>` form is
  rejected. Senders must switch; there is no transition period (no deployment relies on the old form yet).
- A signed timestamp more than 5 minutes from the server's time is rejected as unauthenticated.
- Because the delivery id is signed and deduplicated, a replay inside the window returns the run of the
  first delivery and creates nothing; outside the window it is refused.

Out of scope: per-trigger tolerance settings; signing response bodies; outbound webhook notifications
(OBS-05 → M7).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `automations`: the webhook part of Triggers (AUT-04).

## Impact

`Baton.Domain` (`WebhookSignature`), `Baton.ControlPlane.Api` (webhook endpoint), README sender example.
Breaking for webhook senders (REST contract), not for the public SDK.
