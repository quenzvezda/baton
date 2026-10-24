#!/usr/bin/env bash
# Creates the roadmap milestones (M0–M8, G1, G2) on the current GitHub repository.
# Requires: GitHub CLI (`gh auth login`), run from inside the repo clone. Idempotent: skips existing titles.
set -euo pipefail

milestones=(
  "M0 — Foundation|Solution, migrations, test harness, CI. docs/milestones/M0-foundation.md"
  "M1 — Workflow definitions|WF-01..09 (API). docs/milestones/M1-workflow-definitions.md"
  "M2 — Host registry & access control|HST-01..06 (API), NFR-Security. docs/milestones/M2-host-registry.md"
  "M3 — Task dispatch & leases|ORC-03,04,05,07,08,09,10. docs/milestones/M3-task-dispatch.md"
  "M4 — Automations & run lifecycle|AUT-01..07,09, ORC-01,02,11. docs/milestones/M4-automations-and-run-lifecycle.md"
  "M5 — Web console & monitoring|OBS-01..03, WF-09/HST-06 pages. docs/milestones/M5-web-console-and-monitoring.md"
  "G1 — v1 production readiness|Gate: NFR load/chaos/security. docs/milestones/G1-v1-production-readiness.md"
  "M6 — Scheduling & routing|AUT-08, AUT-10, ORC-06, ORC-12, ORC-13. docs/milestones/M6-scheduling-and-routing.md"
  "M7 — Operability & insight|AUT-11, WF-10, OBS-04..06. docs/milestones/M7-operability-and-insight.md"
  "G2 — Phase 2 exit|Gate: SLOs in production. docs/milestones/G2-phase-2-exit.md"
  "M8 — Extensibility|AUT-12, WF-11, ORC-14, event triggers. docs/milestones/M8-extensibility.md"
)

existing="$(gh api 'repos/{owner}/{repo}/milestones?state=all&per_page=100' --jq '.[].title')"

for entry in "${milestones[@]}"; do
  title="${entry%%|*}"
  description="${entry#*|}"
  if grep -Fxq "$title" <<<"$existing"; then
    echo "skip   $title"
  else
    gh api 'repos/{owner}/{repo}/milestones' -f title="$title" -f description="$description" >/dev/null
    echo "create $title"
  fi
done
