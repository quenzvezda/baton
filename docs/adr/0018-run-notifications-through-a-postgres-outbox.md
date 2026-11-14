# ADR-0018: Run notifications through a Postgres outbox

- **Status:** Proposed
- **Date:** 2026-11-14
- **Deciders:** @Ripan (M7 kickoff 2026-11-14)
- **Spec:** §4 "How a run flows" step 6 · OBS-05 · OQ-08 · OpenSpec change `add-operability-and-insight` (design.md decisions 4, 6, 16–23)

## Context

OBS-05 requires an email or webhook notification when a run fails or times out. A run ends in several
places: the finisher, the reaper and cancellation. Each ends it in one Postgres transaction
(`RunFinalizer`). Sending email or HTTP from inside that transaction would hold the run's row and the
caller's locks across network I/O, and a rollback could not take back a message already sent. Sending
after the commit is the "update then publish" sequence that AGENTS.md forbids: a crash between the two
loses the notification. ADR-0003 rules out a message broker.

Webhook URLs often carry a token (Slack, Teams), so they are secrets. Today only the Control Plane API
can decrypt secrets (ADR-0015).

## Decision

Notifications go through a **transactional outbox in Postgres**, delivered by the orchestrator.

- The statement that ends a run inserts one `notification_deliveries` row per channel of the run's
  automation that fires on the outcome (Failed, TimedOut). It is in the same transaction, so a run that
  ends always has its deliveries, and one that does not end has none.
- `NotificationLoop` runs on every orchestrator replica (ADR-0008 row loop):
  - It claims due rows with `FOR UPDATE SKIP LOCKED` and marks them with a short `locked_until`, then
    commits.
  - It sends outside any transaction and records each result in a transaction of its own.
  - It makes up to 5 attempts, waiting about 1, 5, 15 and 30 minutes between them.
  - Delivery is at least once, and receivers deduplicate on the delivery id.
- **A notification never changes the run's outcome (OQ-08).** An exhausted delivery is recorded as a run
  event and counted in a metric.
- **The orchestrator shares the Data Protection key ring** with the API: application name `baton`, table
  `data_protection_keys`, the same certificate configuration. This widens ADR-0015 to a second process.
  The orchestrator only decrypts, and the API is still the only process that issues secrets.

## Consequences

**Positive**
- Keeps "one transaction per state transition": no lost or phantom notifications.
- No new infrastructure, and the queue of deliveries can be inspected with SQL.
- Retries and replica failover reuse the patterns of the task queue.

**Negative / risks**
- A notification is not instant. It arrives within one loop interval (5 s) of the run's end at best.
  → acceptable for failure alerts.
- The orchestrator needs the key ring certificate. → both processes take the same configuration. A
  missing key fails the delivery visibly (run event, metric), never silently.
- A burst of failures creates a burst of deliveries. → each replica sends at most 20 per tick. Per-channel
  rate limits are added if needed.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Send inside the finishing transaction | Network I/O under row locks on the hot path; a rollback cannot recall a sent message |
| Send after commit from the finishing code | "Update then publish": a crash in between loses the notification |
| A message broker or job framework | Second stateful dependency, against ADR-0003 |
| `LISTEN/NOTIFY` only | Not durable; a notification sent while no listener is connected is lost |
| Deliver from the Control Plane API | The API is a request-serving tier with no loops; loops live in the orchestrator (design spec §8) |
| Notifications as tasks injected into every run | Ties delivery to host groups, slots and agent builds, and would let a failed notification fail the run, which OQ-08 rejects |

## References

- ADR-0003, ADR-0006, ADR-0008, ADR-0015
- OpenSpec change `add-operability-and-insight`, spec `notifications`
