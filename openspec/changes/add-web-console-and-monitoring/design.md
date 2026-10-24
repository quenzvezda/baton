# Design

## Context

See proposal.md. Constraints: ADR-0012 (UI framework, Proposed — accept before starting), the UI talks
only to the Control Plane API; ADR-0011 (history is retained 90 days, so the UI must handle purged runs).

## Goals / Non-Goals

**Goals:** run list first page < 500 ms with 90 days of history; live logs visible within 2 s of being written.

**Non-Goals:** dashboards and alerting (M7); mobile layouts beyond responsive basics.

## Decisions

- **Cursor paging.** Cursor = last `(created_at, id)` seen; ordering by `created_at DESC, id DESC`
  using `ix_runs_by_automation` and a new composite index for status/group filters if the query plan needs it.
- **Live tail.** `GET …/logs` with `Accept: text/event-stream` streams new lines by polling `task_logs`
  by `(task_run_id, attempt, seq)` every 1 s (or LISTEN on a per-run channel if load requires).
  Without SSE it returns the full log for the attempt.
- **DAG rendering.** Graph JSON from the catalog; layout with a small JS library via interop
  (e.g. dagre/elk); node colour by task state with a legend; never colour alone — state text in the node.
- **Parameter form.** Render from JSON Schema for common types (string, number, boolean, enum, date,
  array of strings); fall back to a JSON editor for anything else; server-side validation is authoritative.
- **Roles in UI.** Hide/disable mutating actions per role, but rely on API enforcement (M2).

## Risks / Trade-offs

- [Large logs] → page by sequence ranges; cap SSE backlog.
- [Schema features beyond the form renderer] → JSON editor fallback.
