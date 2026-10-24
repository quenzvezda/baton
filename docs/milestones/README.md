# Milestones

One file per milestone and gate. Each milestone lists its requirement IDs, the OpenSpec change that
specifies it, the ADRs and open questions it depends on, and its **exit criteria** — a milestone is
done when every exit criterion is ticked and its change is archived.

Keep the table below in roadmap order.

| File | Phase |
| --- | --- |
| [M0-foundation.md](M0-foundation.md) | 1 |
| [M1-workflow-definitions.md](M1-workflow-definitions.md) | 1 |
| [M2-host-registry.md](M2-host-registry.md) | 1 |
| [M3-task-dispatch.md](M3-task-dispatch.md) | 1 |
| [M4-automations-and-run-lifecycle.md](M4-automations-and-run-lifecycle.md) | 1 |
| [M5-web-console-and-monitoring.md](M5-web-console-and-monitoring.md) | 1 |
| [G1-v1-production-readiness.md](G1-v1-production-readiness.md) | Gate |
| [M6-scheduling-and-routing.md](M6-scheduling-and-routing.md) | 2 |
| [M7-operability-and-insight.md](M7-operability-and-insight.md) | 2 |
| [G2-phase-2-exit.md](G2-phase-2-exit.md) | Gate |
| [M8-extensibility.md](M8-extensibility.md) | 3 |

## GitHub milestones

Every file here has a GitHub milestone, and these files are the source of truth: change the file first,
then mirror it on GitHub by hand in the same piece of work.

| GitHub field | Value |
| --- | --- |
| Title | the file's `# ` heading, verbatim (`M2 — Host registry, agent protocol & access control`) |
| Description | the file's metadata line without Markdown, then ` · ` and the file path |
| State | open; closed when the owner approves the milestone and its change is archived (see AGENTS.md "How to work") |

Mirror it when a file is added, its heading or metadata line changes, or the milestone closes. Every PR
for a milestone is linked to it.

```bash
gh api 'repos/{owner}/{repo}/milestones?state=all' --jq '.[] | "\(.number) \(.state) \(.title)"'
gh api 'repos/{owner}/{repo}/milestones' -f title="<heading>" -f description="<metadata> · docs/milestones/<file>"
gh api -X PATCH 'repos/{owner}/{repo}/milestones/<number>' -f title="<heading>" -f description="<…>"
gh api -X PATCH 'repos/{owner}/{repo}/milestones/<number>' -f state=closed
gh pr edit <pr> --milestone "<heading>"
```
