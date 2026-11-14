# Tasks

## 1. Schema and domain

- [x] 1.1 Migration `SchedulingAndRouting` (design.md "Migration Plan"): `catch_up_limit` with its CHECKs, `workflow_runs.placement_mode` and `pin_labels`, `task_runs.pinned_run`, drop `task_runs.pinned_host_id`, `ix_runs_pinned`, `cancel_reason` gains `pinned_host_lost`; verify `Schema_matches_baseline` (updated for the new columns) and `Migration_keeps_existing_runs_per_task` (`dotnet test tests/Baton.Integration.Tests --filter Schema`)
- [x] 1.2 `MisfirePlanner` (decision 6) in `Baton.Domain`; verify unit tests `FireOnce_fires_the_most_recent_missed_time`, `Skip_fires_no_missed_time`, `CatchUp_fires_the_most_recent_N_in_time_order`, `Fire_up_to_60_s_late_is_on_time`, `On_time_fire_after_missed_ones_always_fires`, `Long_outage_is_counted_without_unbounded_memory`
- [x] 1.3 Aging formula in `TaskPriority` (ORC-06) with step and cap; verify unit tests for the spec scenarios Waiting task ages (42), Cap (60), Ceiling (100) and `Cap_zero_disables_aging`

## 2. Misfire policy and fire time preview (AUT-08, AUT-10)

- [x] 2.1 Trigger API fields `misfirePolicy` and `catchUpLimit` (decision 7): default 3 for CatchUp, rejected limit outside 1–10 or on another policy, webhook triggers keep the default; verify `Catch_up_limit_out_of_range_is_rejected`, `Catch_up_without_limit_defaults_to_3`, `Misfire_policy_round_trips`
- [x] 2.2 `CronTriggers` fires through `MisfirePlanner` and records `trigger.misfired`; verify Testcontainers tests `FireOnce_after_an_outage_creates_one_run_for_the_latest_missed_time`, `Skip_after_an_outage_creates_no_run_and_records_the_miss`, `CatchUp_creates_the_3_most_recent_missed_runs`, `Slightly_late_fire_is_not_a_misfire`, `Catch_up_runs_follow_the_concurrency_policy`, and that the existing `Two_replicas_firing_the_same_due_trigger_create_one_run` still passes
- [x] 2.3 `GET /api/cron/preview` (decision 8); verify `Preview_returns_the_next_5_fire_times_in_the_zone`, `Preview_of_an_expression_that_stops_firing_returns_fewer`, `Invalid_expression_preview_is_rejected_with_the_save_message`

## 3. Priority aging (ORC-06)

- [x] 3.1 `AgingLoop` (decision 9) with `Dispatch:AgingStep` and `Dispatch:AgingCap` validated at startup; verify Testcontainers tests `Queued_task_ages_one_point_per_step`, `Unchanged_rows_are_not_written` (row versions unchanged by a second tick), `Aged_task_overtakes_a_newer_higher_base_task`, and `Invalid_aging_settings_fail_startup`
- [x] 3.2 Re-queued tasks start from base priority in `RetryPromoter` and `TaskFinisher` (decision 10); verify `Retry_starts_again_from_base_priority`
- [x] 3.3 Aging next to claims never blocks and never double-leases; verify the concurrent-actor integration test `Aging_ticks_during_concurrent_claims_skip_locked_rows` (several claimers plus the loop, every task leased once)
- [x] 3.4 Measure aging under the G1 load profile with a 30 s step so it writes (M6 exit criterion "only changed rows updated"): rows written per tick, `ix_tasks_dispatch` size and p95 Queued→Leased against the G1 report; verify by `docs/reports/m6-aging-load.md` with the numbers and the command used

## 4. Placement mode (ORC-12)

- [x] 4.1 Automation API `placementMode` (create, update, clone, read), copied to the run at creation (decision 11); verify `Default_placement_is_per_task` and `Placement_change_does_not_affect_active_runs`
- [x] 4.2 `RunMaterializer` writes `pin_labels` and `pinned_run`; verify `Pin_labels_are_the_union_of_every_task_label`
- [x] 4.3 Pinning in the claim transaction (decision 12); verify Testcontainers tests `First_claim_pins_the_run`, `Task_of_a_run_pinned_elsewhere_is_not_leased`, `Pinned_host_must_carry_labels_of_the_whole_run`, `Retry_goes_to_the_pinned_host`, and the concurrent-actor test `Concurrent_claims_of_parallel_roots_pin_one_host`
- [x] 4.4 Draining host serves only its pinned runs, and a drained agent keeps claiming (decision 5); verify `Draining_host_takes_tasks_of_its_pinned_run_only` and that the existing `Drained_host_learns_it_from_the_heartbeat_reply_and_is_not_eligible` still passes
- [x] 4.5 Pinned host lost in the reaper (decision 13); verify `Run_fails_when_its_pinned_host_goes_offline`, `Run_fails_when_its_pinned_host_is_moved`, `Pinned_host_restarting_within_30_s_keeps_the_run`; add the pinned-run note to `docs/runbooks/agent-rollout.md`

## 5. Dispatch pause (ORC-13)

- [x] 5.1 `POST /api/host-groups/{id}/pause` and `/resume`, Operator, idempotent, `dispatchPaused` in reads (decision 14); verify `Operator_pauses_and_resumes_a_group`, `Viewer_cannot_pause`, `Pause_twice_is_a_no_op`
- [x] 5.2 Claims of a paused group lease nothing and resume wakes waiting agents; verify Testcontainers tests `Paused_group_leases_nothing_while_running_tasks_finish`, `Runs_are_admitted_while_the_group_is_paused`, `Resume_wakes_a_long_polling_agent_within_2_s`

## 6. Console

- [x] 6.1 Automation editor: misfire select, catch-up limit shown for CatchUp (default 3), placement select (decision 15); verify bUnit tests `Catch_up_limit_appears_only_for_CatchUp` and `Placement_is_saved_and_shown_again`
- [x] 6.2 Editor fire time preview, debounced, with the server's message on invalid input; verify bUnit tests `Preview_lists_the_next_5_fire_times` and `Invalid_expression_shows_the_message_instead_of_times`
- [x] 6.3 Hosts page group rows with paused badge and Pause/Resume for Operator and Admin; verify bUnit tests `Operator_pauses_a_group_from_the_hosts_page` and `Viewer_sees_paused_state_without_actions`

## 7. Close

- [x] 7.1 `dotnet build Baton.slnx -c Release` clean; `dotnet test` green for Domain, Agent, Web and Integration; `npx @fission-ai/openspec@1.14.1 validate --all --strict` green

## Workflow follow-up

- Open the PR linked to the GitHub milestone "M6 — Scheduling & routing controls", every exit criterion ticked except the archive.
- On the owner's OK: `openspec archive add-scheduling-and-routing-controls`, tick the archive criterion, mark M6 done in `ROADMAP.md` and `README.md`, close the GitHub milestone, merge.
