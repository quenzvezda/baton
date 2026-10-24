# Tasks

## 1. Decisions

- [ ] 1.1 Accept ADR-0013 (agent authentication) after security review; verify ADR status is Accepted in `docs/adr/README.md`
- [ ] 1.2 Record answers to OQ-05/OQ-06 in `docs/open-questions.md`; verify reviewed in PR

## 2. Access control

- [ ] 2.1 OIDC sign-in and role mapping (Viewer/Operator/Admin) from IdP claims (NFR-Security); verify API tests: anonymous 401, Viewer read 200, Viewer write 403
- [ ] 2.2 Authorization policies applied to all Control Plane endpoints incl. M1 catalog; verify an endpoint-inventory test fails when an endpoint has no policy

## 3. Host groups

- [ ] 3.1 Host-group CRUD endpoints with Admin role and limit-row upsert (HST-01); verify API tests for create, duplicate (409), delete-in-use and 403
- [ ] 3.2 Agent credential issuance/revocation per group (NFR-Security); verify integration test: revoked token is rejected

## 4. Agent protocol

- [ ] 4.1 `AgentService.Register` with create-or-update by name (HST-02); verify integration tests for first registration, re-registration and unknown group
- [ ] 4.2 gRPC auth interceptor validating group-scoped credentials; verify tests for missing and wrong-group credentials
- [ ] 4.3 `AgentService.Heartbeat` updating `last_heartbeat_at` from `now()` and returning drain state (HST-03, HST-04); verify integration test that `last_heartbeat_at` comes from server time, not the agent
- [ ] 4.4 Offline sweep and eligibility check based on heartbeat age (HST-03); verify integration test: no heartbeat 31 s ⇒ Offline

## 5. Agent

- [ ] 5.1 `AgentOptions`, `AgentClient` with resilience pipeline, register + heartbeat loop; verify agent registers against a locally running orchestrator (manual check documented in README)
- [ ] 5.2 Drain handling and graceful shutdown (stop claiming, finish running work); verify unit test on the worker state machine

## 6. Inventory and operations

- [ ] 6.1 `POST /api/hosts/{id}/drain|undrain` and group move (HST-04, HST-05); verify API tests
- [ ] 6.2 `GET /api/hosts` inventory (HST-06); verify API test with seeded leases

## Workflow follow-up

- Archive after M2 exit criteria are met.
