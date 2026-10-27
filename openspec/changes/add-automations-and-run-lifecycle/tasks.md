# Tasks

## 1. Automations API

- [ ] 1.1 Automation CRUD, clone, enable/disable with Operator role (AUT-01); verify API tests per scenario
- [ ] 1.2 ETag/`If-Match` with `row_version` (AUT-01); verify stale update returns 412
- [ ] 1.3 Workflow selection pinned/latest (AUT-02) and parameter validation against stored JSON Schema (AUT-03); verify validation error names the field
- [ ] 1.4 Routing, priority bounds, max runs, concurrency policy, run timeout fields (AUT-05, AUT-06, AUT-07, AUT-09); verify validation tests
- [ ] 1.5 Workflow run limit API `PUT/GET/DELETE /api/workflows/{key}/limit` (ORC-02, design.md decision 4); verify API tests
- [ ] 1.6 OQ-07 secret guidance for definition authors (design.md decision 1); verify the README section is linked from the params rules

## 2. Triggers

- [x] 2.1 Cron next-fire computation with IANA zones (AUT-04); verify unit tests incl. a DST transition zone
- [ ] 2.2 `TriggerLoop` (1 s, SKIP LOCKED) with idempotent run creation (ORC-01); verify two-replica integration test creates one run
- [x] 2.3 Concurrency policies Skip/Queue/Replace/Allow (AUT-07); verify one integration test per policy
- [ ] 2.4 Run now endpoint `POST /api/automations/{id}/runs` (AUT-04); verify API test
- [ ] 2.5 Webhook intake with HMAC verification, encrypted secrets (ADR-0015) and delivery dedupe (AUT-04, ORC-01); verify valid/invalid signature and retried-delivery tests
- [ ] 2.6 Cron accuracy (NFR-TriggerAccuracy); verify integration test: due trigger has a run within 5 s

## 3. Run lifecycle

- [x] 3.1 `AdmissionLoop` (1 s, advisory lock per group), key-ordered limit reservation, pass-over (ORC-02); verify group-full and pass-over integration tests and a deadlock-free two-replica test
- [x] 3.2 Run start: task runs created, roots Queued, deadline set, NOTIFY (ORC-02); verify integration test
- [x] 3.3 Finalization hook: outcome rule and limit release (Run outcome); verify only-skips and one-failure tests
- [x] 3.4 Run timeout in reaper (AUT-09); verify deadline test
- [ ] 3.5 Cancellation API and flow incl. grace period and no-retry (ORC-11); verify the three cancellation scenarios
- [x] 3.6 `LimitReconcilerLoop` (5 min) recomputing `in_use`; verify drift-correction test
- [x] 3.7 Run events for every transition (NFR-Auditability); verify timeline test

## 4. End-to-end

- [ ] 4.1 E2E: cron automation → run → tasks on two agents → Succeeded, using the design-spec example workflow; verify in integration suite

## Workflow follow-up

- Archive after M4 exit criteria are met.
