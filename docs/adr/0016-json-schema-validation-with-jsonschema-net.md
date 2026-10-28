# ADR-0016: Validate automation parameters with JsonSchema.Net

- **Status:** Accepted
- **Date:** 2026-10-28 (proposed 2026-10-27)
- **Deciders:** @Ripan (M4 kickoff 2026-10-27, owner review of M4 2026-10-28)
- **Spec:** AUT-03 · OpenSpec change `add-automations-and-run-lifecycle` (design.md decision 13)

## Context

Each registered definition stores a JSON Schema of its params type, exported by
`System.Text.Json.Schema.JsonSchemaExporter` (M1). Saving an automation must validate its parameters
against that schema and name every failing field (AUT-03). .NET exports schemas but has no validator.

## Decision

Validate with **JsonSchema.Net** (json-everything, MIT), pinned in `Directory.Packages.props`.

- The Control Plane API evaluates the stored schema (draft 2020-12 semantics) with `OutputFormat.List`
  and reports leaf errors keyed by field: `parameters.region`, `parameters.recipients[0]`. A missing
  required property is reported under its own name.
- Format assertions (`date`, `date-time`, …) are enforced, matching what System.Text.Json accepts when the
  agent deserializes the params.

## Consequences

**Positive**
- Full JSON Schema support, so later SDK features (enums, nested records, polymorphism) need no
  validator work.

**Negative / risks**
- A third-party dependency on the API → small, actively maintained, no transitive dependencies outside
  the json-everything family; one bump per PR.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Hand-written validator for the exporter's subset | Has to track every schema shape the exporter may emit; easy to get subtly wrong. |
| Deserialize into the params type | The API may not carry every version's params type; errors do not name fields reliably. |
| NJsonSchema | Pulls in Newtonsoft.Json. |

## References

- M1 `WorkflowJson.TryExportParamsSchema`; `openspec/specs/workflow-definitions` "Parameter schema"
