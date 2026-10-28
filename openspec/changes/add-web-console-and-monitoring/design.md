# Design

## Context

See proposal.md. Constraints: ADR-0012 (UI framework, Accepted 2026-10-28), the UI talks
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
- **DAG rendering.** Graph JSON from the catalog; layered layout computed in C# and drawn as inline SVG
  (decision 3 below); node colour by task state with a legend; never colour alone — state text in the node.
- **Parameter form.** Render from JSON Schema for common types (string, number, boolean, enum, date,
  array of strings); fall back to a JSON editor for anything else; server-side validation is authoritative.
- **Roles in UI.** Hide/disable mutating actions per role, but rely on API enforcement (M2).

## Risks / Trade-offs

- [Large logs] → page by sequence ranges; cap SSE backlog.
- [Schema features beyond the form renderer] → JSON editor fallback.

## Decisions agreed with the owner (2026-10-28)

1. **ADR-0012 accepted: Blazor Server** (closes OQ-09). Interactive server render mode; the console
   calls the Control Plane API only.
2. **Sign-in and local IdP.** OIDC authorization-code + PKCE with a cookie session
   (`Microsoft.AspNetCore.Authentication.OpenIdConnect`), `SaveTokens`; the access token is forwarded to the
   API by a delegating handler and refreshed with the refresh token shortly before expiry. Local
   development uses Keycloak from `docker-compose.yml` with an imported `baton` realm (ADR-0017).
3. **DAG layout in C#, no JS library.** Longest-path layering, barycentre ordering within a layer, SVG
   rendered by a Razor component. Testable with bUnit.
4. **Component tests with bUnit** in a new `tests/Baton.Web.Tests` project (test-only package).
5. **Live tail by polling.** The API's SSE stream polls `task_logs` every second by
   `(task_run_id, attempt, seq)`, emits each line as an event whose `id` is its `seq` (so
   `Last-Event-ID` resumes), and ends with an `end` event once the attempt has finished and every line has
   been sent. The Blazor server consumes the stream and pushes lines over the circuit; tokens stay on the
   server. LISTEN/NOTIFY is deferred until load needs it.
6. **`GET /api/me`** (Viewer) returns the caller's name and effective roles (hierarchy applied), so the
   claim-to-role mapping stays in the API only.
7. **No Retry action in M5.** Re-running a failed run is WF-10 (M7). The role-aware scenario names Cancel
   only; Retry arrives with WF-10.
8. **Smaller defaults.**
   - Runs cursor: opaque base64url of `created_at` (ticks) and `id`; page size 50 by default, at most 200.
     Ordering `created_at DESC, id DESC`. A composite index is added only if a query plan needs it.
   - Logs read: `?attempt=` (default: latest attempt), `afterSeq=` and `limit=` (default 1000, at most
     5000) for paging large logs.
   - A run that does not exist or was purged (ADR-0011) returns 404; the console shows "run not available".
   - The 10k-run paging check and the first-page timing run as integration tests against seeded runs.
