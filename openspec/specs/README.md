# Specs — current truth

This folder describes behavior that is **built and shipped**. It starts empty: capabilities appear
here when their change is archived (`openspec archive <change-id>`).

Planned capabilities (created by the phase-1 changes in `../changes/`):

| Capability | Requirement IDs | Created by |
| --- | --- | --- |
| `workflow-definitions` | WF-01..08, WF-09 (catalog API) | `add-workflow-definitions` (M1) |
| `host-registry` | HST-01..05, HST-06 (inventory API), NFR-Security (agents) | `add-host-registry` (M2) |
| `task-dispatch` | ORC-03..05, ORC-07..10 | `add-task-dispatch` (M3) |
| `automations` | AUT-01..07 | `add-automations-and-run-lifecycle` (M4) |
| `run-lifecycle` | ORC-01, ORC-02, ORC-11, AUT-09 | `add-automations-and-run-lifecycle` (M4) |
| `access-control` | NFR-Security (people) | `add-host-registry` (M2) |
| `run-monitoring` | OBS-01..03 | `add-web-console-and-monitoring` (M5) |
| `web-console` | WF-09, HST-06, AUT-03 (form), OBS-01..03 (pages) | `add-web-console-and-monitoring` (M5) |
