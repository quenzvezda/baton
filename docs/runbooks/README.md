# Runbooks

What to do when Baton misbehaves in a deployment. Each runbook starts from a symptom, lists checks in the
order that finds the cause fastest, and ends with how to confirm recovery. Written for Gate G1 against the
v1 behavior in `openspec/specs/`; the G1 load and chaos reports (`docs/reports/`) show how the system
behaves under the same failures.

| Runbook | Symptom |
| --- | --- |
| [Stuck queue](stuck-queue.md) | Runs stay Pending, or tasks stay Queued, while hosts look idle |
| [Lease-expiry spike](lease-expiry-spike.md) | Many attempts end `lost`; tasks retry without a handler failure |
| [Postgres outage and restart](postgres-outage.md) | API 5xx, orchestrator `/healthz` unhealthy, agents log failed heartbeats |
| [Agent rollout](agent-rollout.md) | Upgrading or replacing agents without failing runs |
| [Backup and restore](backup-restore.md) | Taking, protecting and restoring a backup |

## Signals

| Signal | Where | Meaning |
| --- | --- | --- |
| Orchestrator `/healthz` | `GET http://<orchestrator>:5200/healthz` | `ok`; `degraded` when history partitions run low (`history-partitions` check); `unhealthy` when Postgres is unreachable |
| `baton.limits.corrections` | OpenTelemetry metric, tag `limit.kind` | The limit reconciler fixed a drifted counter — always a bug; alert on any increase |
| `baton.history.partitions_dropped`, `baton.history.runs_purged` | OpenTelemetry metrics | Daily 90-day retention ran |
| Warning `Limit {Key} counted {Was} in use, live rows say {Now}; corrected` | orchestrator log | Same event as the corrections metric, with the key |
| Warning `LISTEN connection failed; claims fall back to polling` | orchestrator log | Wake-ups are lost until it reconnects; claims still re-check every 2 s |
| Error `{Loop} tick failed` | orchestrator log | A loop tick rolled back; the next tick retries |
| Warning `Heartbeat of {Host} failed` | agent log | The agent cannot reach an orchestrator |
| Warning `{Method} failed: database unavailable (…); answered UNAVAILABLE` | orchestrator log | Postgres unreachable during an agent call; the agent retries |

Metrics are exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`src/Shared/BatonTelemetry.cs`).
Suggested alert rules: any increase of `baton.limits.corrections` over 15 min; `/healthz` not `ok` for
5 min; `lost` attempts per hour above the normal baseline (query in [lease-expiry-spike](lease-expiry-spike.md)).

## Conventions

SQL below is read-only unless marked **write**; run writes only with a second person and after a backup.
Times are Postgres `now()`, never a host clock. `<group>` is a host group name.
