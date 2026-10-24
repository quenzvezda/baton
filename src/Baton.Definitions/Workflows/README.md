# Workflows

One folder per workflow: the `WorkflowDefinition<TParams>` class, its params record and its handlers.
The `nightly-sales-report` example in design spec §5 is the reference shape (it is compiled in
`tests/Baton.Domain.Tests/Samples`).

The Control Plane API validates every definition at startup and refuses to start if one is invalid
(WF-03), then registers it as `key@version` with a SHA-256 of its canonical graph (WF-04).

## Definitions

- `Key`: kebab-case, at most 64 characters, never reused for a different workflow.
- `Version`: starts at 1. A registered `key@version` is immutable (ADR-0004): **if the hash changes,
  bump `Version`**, or startup fails with "already registered with a different graph".
- `DisplayName`: optional override; defaults to the key in words. It is a label, so changing it needs
  no version bump.
- Task keys: kebab-case, at most 64 characters, unique in the workflow. The graph must be acyclic and
  have at least one task without dependencies.

### What changes the hash (bump `Version`)

- Adding, removing or renaming a task; changing `DependsOn`.
- Any task option: `Retry`, `Timeout`, `When`, `RequireLabels`, `PriorityOffset`.
- Renaming or moving a handler or the params type: both are recorded by namespace-qualified name.

Not part of the hash: the display name, declaration order, and the code inside handlers. A handler's
behavior can change without a version bump — keep it compatible with runs already in flight.

### Defaults and bounds

| Option | Default | Bound |
| --- | --- | --- |
| Attempts (`Retry`, first attempt included) | 3 | 1–10 |
| Initial delay / multiplier / max delay | 30 s / ×2 / 10 min | delay ≥ 0, multiplier ≥ 1, max ≥ initial |
| `Timeout` (per attempt) | 1 h | > 0, ≤ 24 h |
| `When` | `AllSuccess` | — |
| `PriorityOffset` | 0 | −100…100 |

Defaults are written into the hash. They are frozen: changing one would invalidate every definition
that relies on it.

## Params

- An immutable, non-abstract `record`. Serialized with web defaults: properties are **camelCase** in
  JSON and in the generated JSON Schema that the UI renders as the automation form (AUT-03).
- Nullable annotations are enforced, and constructor parameters without a default are **required**.
  Give optional values a default (`DateOnly? ReportDate = null`).
- Only JSON-serializable members: no `Type`, delegates, pointers or streams. Startup fails otherwise.
- Never put secrets in params; reference them by name (design spec §3).

## Handlers

- `public sealed class` implementing `ITaskHandler<TParams, TOutput>` with the workflow's `TParams`.
  `AddWorkflows` registers public handlers of this assembly (scoped, one instance per attempt), so
  constructor dependencies come from DI.
- Tasks run **at least once**: make side effects idempotent with `ctx.IdempotencyKey`
  (`runId:taskKey`, the same on every attempt) — ADR-0006.
- `ctx.GetOutput<T>("task-key")` reads ancestors only; any other key throws at run time.
- The output is stored as JSON, at most **256 KB**; larger fails the task without retry. Put large data
  in blob storage and return its URI.
- Throw `NonRetryableException` for failures a retry cannot fix (bad input, missing record): the task
  fails at once instead of using its remaining attempts (WF-08).
- Logging: write to `ctx.Logger`; it is captured into the task's logs. The repository treats CA1848 as
  an error, so use `[LoggerMessage]` source-generated methods (`static partial void LogRendering(...)`
  on a `partial` handler class) rather than `LogInformation(...)`.
