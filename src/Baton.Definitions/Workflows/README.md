# Workflows

One folder per workflow: the `WorkflowDefinition<TParams>` class, its params record and its handlers.

- Bump `Version` on **any** change to the graph or task options (ADR-0004). Startup fails otherwise.
- Handlers must be idempotent; use `ctx.IdempotencyKey` for side effects (ADR-0006).
- Never put secrets in params; reference them by name (design spec §3).

The `nightly-sales-report` example in design spec §5 is the reference shape.
