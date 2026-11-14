# Re-run a failed run

**Symptom:** a run ended **Failed** or **TimedOut** because of a transient cause, for example a database
that was down or a host that was lost. Running the whole workflow again would repeat work that already
succeeded.

Re-running (WF-10) puts the same run back to Pending. Tasks that succeeded keep their outputs and do not
run again. Failed and cancelled tasks, and the tasks they skipped, run again with their full retry budget.

## Before you re-run

1. **Fix the cause first.** Open the run in the console and read the failed task's last error and its
   logs. A re-run against the same broken dependency fails the same way.
2. **Check that the handlers are idempotent for this run.** A re-run calls each handler again with the
   same `TaskContext.IdempotencyKey` (`<runId>:<taskKey>`) as its earlier attempts. A handler that treats
   "key already seen" as "already done" may skip the work that failed. Ask the workflow's owner if unsure.
3. **Know what it will hold.** A re-run goes through admission like a new run. It waits for the
   automation's run limit, the workflow limit and the group limit, and it does not start while the group's
   dispatch is paused. Its run timeout counts from the new start.

## Re-run

- Console: open the run and choose **Re-run failed tasks** (Operator or Admin).
- API: `POST /api/runs/<runId>/retry-failed` with an Operator token. The response is `202` with the run,
  now `pending`. You get `409` when the run is not Failed or TimedOut.

A Cancelled run cannot be re-run, because a person chose to stop it. Start a new run instead (Run now).

## Confirm

- The run detail shows the re-run count, and the run moves from Pending to Running to its outcome.
- The task that runs again shows its new attempts after the earlier ones (for example 3 and 4 after 1 and
  2). Earlier attempts and their logs stay readable for as long as the run is retained.
- The timeline shows `run.rerun_requested` with who asked.

```sql
-- Re-runs and their outcome (read-only).
SELECT id, status, reruns, started_at, finished_at FROM workflow_runs WHERE id = '<runId>';
SELECT at, type, data FROM run_events WHERE workflow_run_id = '<runId>' ORDER BY id;
```

## If it fails again

The run ends Failed again and its notification channels fire again (OBS-05). It can be re-run again once
the cause is fixed. A run whose history is near the 90-day retention edge may lose the history of its
earliest attempts when their month's partition is dropped. The run itself and its task outputs stay.
