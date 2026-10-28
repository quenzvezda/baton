# Open questions

From design spec §10. Each question has an owner and the milestone it blocks; when answered,
record the answer here and — if it changes architecture — in an ADR.

| ID | Question | Affects | Needed by | Owner | Status |
| --- | --- | --- | --- | --- | --- |
| OQ-01 | Expected scale: runs/day, peak concurrent tasks, number of hosts? Sets the NFR targets in §3. | NFRs, Gate G1 load test | before G1 | @Ripan | Open |
| OQ-02 | Task duration: seconds or hours? Multi-hour tasks may need checkpointing. | ADR-0006, lease tuning | M3 | owner | **Answered 2026-10-26:** lease 60 s / heartbeat 10 s by default, tunable per group (`lease_seconds` 30–3600, heartbeat `min(10 s, lease / 6)`); checkpointing out of scope (M1 bounds: ≤ 10 attempts, ≤ 24 h, default timeout 1 h). Final defaults confirmed by the G1 load test |
| OQ-03 | Do any workflows need every task on one host (local files)? Would make PinnedRun (ORC-12) a Must. | M6 scope, ORC-12 priority | before M3 closes | owner | **Answered 2026-10-26:** not yet ⇒ PinnedRun stays in M6 (Should); the claim query already honors `pinned_host_id` |
| OQ-04 | When a pinned host dies: fail the run, or re-pin and restart from the start? | ORC-12 | M6 | — | Open |
| OQ-05 | Can a host belong to more than one group? v1 assumes one (HST-05). | HST-05, claim query | M2 | @Ripan | **Answered 2026-10-25:** one group per host in v1 |
| OQ-06 | Where do agents run — VMs, containers or Kubernetes? (Kubernetes could scale agents on queue depth.) | ADR-0013, deployment | M2 | @Ripan | **Answered 2026-10-25:** any of them; nothing may assume a PKI or stable host names ⇒ group-scoped tokens (ADR-0013), offline hosts can be removed |
| OQ-07 | Which secret store do handlers read from (Azure Key Vault, HashiCorp Vault, Kubernetes secrets)? | Definitions SDK, M4 | M4 | owner | **Answered 2026-10-27:** Baton ships none. Params carry secret *names*; handlers resolve them through the agent's `IConfiguration`, whose providers are chosen per deployment (`src/Baton.Definitions/Workflows/README.md` "Secrets") |
| OQ-08 | Should a failed notification task fail the whole run, or only be reported? | Run outcome rule, OBS-05 | M7 | — | Open |
| OQ-09 | UI framework: Blazor Server (all-C#) or React (separate frontend team)? | ADR-0012 | before M5 | @Ripan | **Answered 2026-10-28:** Blazor Server (ADR-0012 Accepted) |
