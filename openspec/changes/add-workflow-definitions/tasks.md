# Tasks

## 1. SDK surface

- [ ] 1.1 Finalize `Baton.Abstractions` (builder interfaces, `TaskContext`, `TriggerRule`, `NonRetryableException`); verify the design-spec `NightlySalesReport` example compiles in a test project
- [ ] 1.2 Document authoring rules in `src/Baton.Definitions/Workflows/README.md`; verify reviewed in PR

## 2. Graph and validation

- [ ] 2.1 Implement builder → immutable graph with defaults (WF-05); verify `GraphBuilderTests` cover defaults and declared options
- [ ] 2.2 Implement validation: Kahn cycle detection, root presence, key rules, bounds (WF-03); verify one failing test per scenario in the spec
- [ ] 2.3 Implement trigger-rule evaluation as a pure function (WF-06); verify `TriggerRuleTests` for all rules × upstream mixes, including Skipped propagation

## 3. Hashing and registration

- [ ] 3.1 Canonical JSON serializer + SHA-256 hash (WF-04); verify golden-file test and "same graph, different declaration order ⇒ same hash"
- [ ] 3.2 Params JSON Schema export (AUT-03); verify schema snapshot test for a sample params record
- [ ] 3.3 `AddWorkflows(assembly)` DI registration + handler resolution checks (WF-03); verify unregistered/mismatched handler test
- [ ] 3.4 Startup registration upsert (insert / no-op / fail on changed hash); verify integration tests for all three paths

## 4. Runtime data contract

- [ ] 4.1 `TaskContext.GetOutput` ancestor check and 256 KB output limit (WF-07); verify unit tests for sibling access and oversized output
- [ ] 4.2 Idempotency key `runId:taskKey`; verify unit test across attempts

## 5. Catalog API

- [ ] 5.1 `GET /api/workflows` and `GET /api/workflows/{key}/versions/{version}` (WF-09); verify API tests for latest version, version history and 404

## Workflow follow-up

- Archive after M1 exit criteria in `docs/milestones/M1-workflow-definitions.md` are met.
