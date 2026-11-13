# Gate G1 — v1 production readiness

**Between:** phase 1 and phase 2 · **Inputs:** M0–M5 done

Phase 2 starts only when every item below is checked. Targets come from design spec §3, revised by the
OQ-01 answer (2026-11-14, `docs/open-questions.md`): Baton is a showcase / proof of concept, so the
gate runs on a single machine with `docker compose` and needs no production infrastructure.

| Design spec §3 target | G1 target |
| --- | --- |
| 100k task runs/day, peaks of 50 dispatches/s | ~10k task runs/day, peaks of 20 dispatches/s |
| p95 Queued→Leased < 2 s; cron within 5 s | Unchanged |
| At-least-once; no task twice at once; no lost run | Unchanged |
| 99.9% availability; Postgres primary + streaming replica | Best effort; Postgres restart instead of failover |
| 90-day retention | Unchanged |

## Criteria

**Scope**
- [x] All six phase-1 OpenSpec changes archived; `openspec/specs/` describes v1
- [x] OQ-01 answered; NFR targets in design spec §3 confirmed or revised

**Performance** (load-test report attached)
- [x] 10k task runs/day profile with 20 dispatches/s peaks sustained for 15 min ([report](../reports/g1-load-test.md): 20.0/s)
- [x] p95 Queued→Leased < 2 s with free slots; cron runs created within 5 s (p95 14 ms; cron ≤ 61 ms)

**Resilience** (chaos report attached)
- [x] Kill an agent, kill one of two orchestrator replicas, and restart Postgres under load: no accepted run lost, no task running twice concurrently ([report](../reports/g1-chaos-test.md))
- [x] Rolling agent upgrade under load fails no runs (drain + definition-hash matching) ([report](../reports/g1-chaos-test.md); hash matching by the M3 claim tests)

**Security**
- [x] OIDC roles, agent credentials and webhook HMAC reviewed; no secrets in parameters or logs ([report](../reports/g1-security-review.md): Medium findings fixed, Low items recorded)

**Operations**
- [x] Partition creation and 90-day retention running; limit reconciler alerting on corrections (`add-history-retention`)
- [x] Runbooks: stuck queue, lease-expiry spike, Postgres outage and restart, agent rollout ([`docs/runbooks/`](../runbooks/README.md))
- [x] Backup (`pg_dump`) restored into an empty database and verified (`G1BackupTests`, [runbook](../runbooks/backup-restore.md))
