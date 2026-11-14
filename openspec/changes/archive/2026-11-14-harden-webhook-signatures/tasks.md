# Tasks

## 1. Signature

- [x] 1.1 `WebhookSignature.Compute/Verify` over `t.deliveryId.body` with the `t=…,v1=…` header and a 5-minute window; verify `WebhookSignatureTests` (reference value from `openssl`, stale and future timestamps, other delivery id, old `sha256=` form, multiple `v1`)
- [x] 1.2 Webhook endpoint verifies against Postgres `now()` and the request's delivery id; verify `Stale_timestamp_is_401_and_creates_no_run`, `Replay_with_another_delivery_id_is_401` and the existing `WebhookTests`

## 2. Docs

- [x] 2.1 README sender example signs timestamp and delivery id; verify by running the example's `openssl` line in `WebhookSignatureTests`' reference test
- [x] 2.2 Security review M2 marked fixed in `docs/reports/g1-security-review.md`

## 3. Close

- [x] 3.1 `dotnet build` clean, `dotnet test` green, `openspec validate --all --strict` green
