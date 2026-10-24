# Proposal

## Why

Milestone **M5 — Web console & run monitoring**. The backend is usable through the API after M4, but
users need to configure automations and understand runs without curl. This is the last phase-1
milestone before Gate G1. Requirements: OBS-01, OBS-02, OBS-03, WF-09 (page), HST-06 (page),
AUT-03 (generated form).

## What Changes

- Runs query API with filters and cursor paging; run detail API with tasks, attempts, hosts, timings, outputs and errors.
- Log read API per task attempt with live tail over Server-Sent Events.
- Blazor Server console (ADR-0012 must be accepted first): workflow catalog with DAG, automation list and
  editor with a parameter form generated from the JSON Schema, runs list, run detail with a DAG coloured by
  task state, live logs, hosts page. Role-aware actions.

Out of scope: queue dashboard OBS-04, notifications OBS-05, OpenTelemetry export OBS-06 (M7);
cron preview AUT-10 (M6).

## Capabilities

### New Capabilities

- `run-monitoring`: querying runs, run detail and task logs through the API, including live log tailing.
- `web-console`: the browser UI for catalog, automations, runs, logs and hosts.

### Modified Capabilities

None.

## Impact

`Baton.ControlPlane.Api` (runs and logs endpoints), `Baton.Web`. ADR-0012.
