# Tasks

## 1. Decisions

- [x] 1.1 Accept ADR-0012 (UI framework) and close OQ-09; verify ADR status in `docs/adr/README.md`

## 2. Monitoring API

- [ ] 2.1 `GET /api/runs` with filters and cursor paging (OBS-01); verify paging test without duplicates/gaps on 10k seeded runs
- [ ] 2.2 `GET /api/runs/{id}` detail with tasks and attempts (OBS-02); verify retried-task test
- [ ] 2.3 `GET /api/runs/{id}/tasks/{key}/logs?attempt=` full read and SSE live tail (OBS-03); verify ordering test and 2 s live-tail test

## 3. Console

- [ ] 3.1 Sign-in (OIDC, Keycloak dev realm per ADR-0017), `GET /api/me`, layout, navigation and typed API client; verify login round-trip against the dev IdP
- [ ] 3.2 Workflow catalog with DAG rendering and version selector (WF-09); verify component test renders the example DAG
- [ ] 3.3 Automations list and editor with schema-generated form and server error mapping (AUT-03); verify component tests for required fields and server errors
- [ ] 3.4 Runs list with filters (OBS-01); verify component test
- [ ] 3.5 Run detail with state-coloured DAG and per-task attempts (OBS-02); verify component test incl. failed and skipped states
- [ ] 3.6 Log viewer with live follow (OBS-03); verify manual check documented and component test with a fake stream
- [ ] 3.7 Hosts page with drain/undrain (HST-06); verify component test
- [ ] 3.8 Role-aware actions (NFR-Security); verify Viewer sees no mutating actions

## Workflow follow-up

- Archive after M5 exit criteria are met; then run Gate G1.
