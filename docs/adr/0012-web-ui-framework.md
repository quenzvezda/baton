# ADR-0012: Web UI framework

- **Status:** Accepted
- **Date:** 2026-10-28 (proposed 2026-10-24)
- **Deciders:** @Ripan (M5 kickoff 2026-10-28)
- **Spec:** §4 Components, open question OQ-09 · OpenSpec change `add-web-console-and-monitoring`

## Context

The UI covers the automation editor (form generated from a JSON Schema), workflow catalog with DAG
rendering, run detail with live logs (SSE), hosts and queue dashboards. The design spec proposes
Blazor Server with React as the alternative and leaves the choice open.

## Decision

Use **Blazor Server** (`Baton.Web`, interactive server render mode) for v1. It talks to the Control Plane
API only (no direct DB access), so the UI can be replaced without backend changes.

- **Sign-in:** OIDC authorization-code flow with a cookie session
  (`Microsoft.AspNetCore.Authentication.OpenIdConnect`); the access token is saved in the session and
  forwarded to the API as a bearer token (ADR-0014). Tokens never reach the browser.
- **Live data:** the Blazor server reads the API's Server-Sent Events stream and pushes lines over the
  circuit; the browser never calls the API directly.
- **DAG rendering:** a small layered layout computed in C# (longest-path layers, barycentre ordering) and
  rendered as inline SVG by a Razor component — no JavaScript library. Workflow DAGs are small, and the
  layout stays unit-testable.
- **Component tests:** **bUnit** in `tests/Baton.Web.Tests` (test-only dependency).
- **Roles in the UI:** the console asks `GET /api/me` for the signed-in person's effective roles, so the
  claim-to-role mapping (ADR-0014) lives only in the API. The UI hides mutating actions; the API still
  enforces every policy.

## Consequences

**Positive**
- All-C# stack; shared DTO shapes; one toolchain for the team.
- No npm toolchain and no JS dependency to pin or audit.

**Negative / risks**
- Server-side circuits per user → fine for an internal ops tool.
- A hand-written layout is less polished than dagre/elk for large graphs → acceptable for v1 DAG sizes;
  swap in a JS layout via interop if graphs grow.

## Alternatives considered

| Option | Why not (yet) |
| --- | --- |
| React + Vite SPA | Better fit for a dedicated frontend team; adds a second toolchain. |
| Blazor WebAssembly | Larger download; tokens in the browser; no clear benefit for an internal tool. |
| dagre / elkjs via JS interop | Extra vendored JS and interop that bUnit cannot render; not needed at v1 graph sizes. |
