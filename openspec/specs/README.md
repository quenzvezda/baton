# Specs — current truth

This folder describes behavior that is **built and shipped**. Capabilities appear here when their
change is archived (`openspec archive <change-id>`); never edit them by hand for in-flight work.

Phase-1 capabilities (created by the changes in `../changes/`):

| Capability | Requirement IDs | Created by | Status |
| --- | --- | --- | --- |
| `workflow-definitions` | WF-01..08, WF-09 (catalog API) | `add-workflow-definitions` (M1) | Built (2026-10-24) |
| `host-registry` | HST-01..05, HST-06 (inventory API), NFR-Security (agents), ORC-13 | `add-host-registry` (M2); dispatch pause and drain with pinned runs by `add-scheduling-and-routing-controls` (M6) | Built (2026-10-25; 2026-11-14) |
| `task-dispatch` | ORC-03..10, ORC-12 | `add-task-dispatch` (M3); aging and run pinning by `add-scheduling-and-routing-controls` (M6) | Built (2026-10-27; 2026-11-14) |
| `automations` | AUT-01..08, AUT-10, ORC-12 (placement) | `add-automations-and-run-lifecycle` (M4); AUT-04 signature by `harden-webhook-signatures` (G1); misfire, preview and placement by `add-scheduling-and-routing-controls` (M6) | Built (2026-10-28; 2026-11-14) |
| `run-lifecycle` | ORC-01, ORC-02, ORC-11, AUT-09 | `add-automations-and-run-lifecycle` (M4) | Built (2026-10-28) |
| `access-control` | NFR-Security (people) | `add-host-registry` (M2) | Built (2026-10-25) |
| `run-monitoring` | OBS-01..03 | `add-web-console-and-monitoring` (M5) | Built (2026-10-29) |
| `web-console` | WF-09, HST-06, AUT-03 (form), OBS-01..03 (pages), AUT-08, AUT-10, ORC-12, ORC-13 (forms) | `add-web-console-and-monitoring` (M5); scheduling fields and group pause by `add-scheduling-and-routing-controls` (M6) | Built (2026-10-29; 2026-11-14) |
| `operations` | NFR-Retention, NFR-Auditability (limit corrections) | `add-history-retention` (G1) | Built (2026-11-14) |
