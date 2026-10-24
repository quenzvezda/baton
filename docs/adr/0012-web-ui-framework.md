# ADR-0012: Web UI framework

- **Status:** Proposed
- **Date:** 2026-10-24
- **Deciders:** @Ripan (pending team input)
- **Spec:** §4 Components, open question OQ-09

## Context

The UI covers the automation editor (form generated from a JSON Schema), workflow catalog with DAG
rendering, run detail with live logs (SSE), hosts and queue dashboards. The design spec proposes
Blazor Server with React as the alternative and leaves the choice open.

## Decision (proposed)

Use **Blazor Server** (`Baton.Web`) for v1; it talks to the Control Plane API only (no direct DB access),
so the UI can be replaced without backend changes. The skeleton is scaffolded with Blazor Server.

To be accepted before M5 starts. If a separate frontend team owns the UI, supersede with React + Vite.

## Consequences

**Positive**
- All-C# stack; shared DTOs; one toolchain for the team.

**Negative / risks**
- Server-side circuits per user → fine for an internal ops tool.
- DAG visualization needs a JS library via interop either way.

## Alternatives considered

| Option | Why not (yet) |
| --- | --- |
| React + Vite SPA | Better fit for a dedicated frontend team; adds a second toolchain. |
| Blazor WebAssembly | Larger download; no clear benefit for an internal tool. |
