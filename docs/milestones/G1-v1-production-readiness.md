# Gate G1 — v1 production readiness

**Between:** phase 1 and phase 2 · **Inputs:** M0–M5 done

Phase 2 starts only when every item below is checked. Targets come from design spec §3 and must be
confirmed against OQ-01 (expected scale) first.

## Criteria

**Scope**
- [ ] All six phase-1 OpenSpec changes archived; `openspec/specs/` describes v1
- [ ] OQ-01 answered; NFR targets in design spec §3 confirmed or revised

**Performance** (load-test report attached)
- [ ] 100k task runs/day profile with 50 dispatches/s peaks sustained for 30 min
- [ ] p95 Queued→Leased < 2 s with free slots; cron runs created within 5 s

**Resilience** (chaos report attached)
- [ ] Kill an agent, an orchestrator replica, and fail over Postgres: no accepted run lost, no task running twice concurrently
- [ ] Rolling agent upgrade under load fails no runs (drain + definition-hash matching)

**Security**
- [ ] OIDC roles, agent credentials and webhook HMAC reviewed; no secrets in parameters or logs

**Operations**
- [ ] Partition creation and 90-day retention running; limit reconciler alerting on corrections
- [ ] Runbooks: stuck queue, lease-expiry spike, Postgres failover, agent rollout
- [ ] Backups and restore tested
