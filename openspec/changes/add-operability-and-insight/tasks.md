# Tasks

## 1. Schema, shared wiring and domain

- [x] 1.1 Migration `OperabilityAndInsight` (design.md "Migration Plan"): `audit_log`, `notification_channels`, `notification_deliveries`, `ix_deliveries_due`, `workflow_runs.reruns` and `trace_parent`, `ix_attempts_lost`; verify `Schema_matches_baseline` (updated) and `Lost_attempt_index_exists_on_every_partition` (`dotnet test tests/Baton.Integration.Tests --filter Schema`)
- [x] 1.2 `RerunPlan` in `Baton.Domain` (decision 8); verify unit tests `Last_task_failed_resets_it_and_its_skipped_downstream`, `Succeeded_failure_only_task_is_kept`, `Skipped_task_with_kept_upstream_stays_skipped`, `Diamond_resets_only_the_failed_branch_and_the_join`, `Timed_out_run_resets_cancelled_waiting_tasks`, `Reset_task_whose_rule_no_longer_fires_is_skipped_and_propagates`
- [x] 1.3 `NotificationSchedule.Delay` in `Baton.Domain` (decision 19); verify unit tests `Delays_are_1_5_15_30_minutes_with_jitter` and `Fifth_failure_is_final`
- [x] 1.4 Data Protection registration (`AddBatonDataProtection`) and `KeyRingProtection` moved to `Baton.Persistence/Security`, used by API and orchestrator; `NotificationSecrets` in `Baton.Persistence` (decision 23); verify `Secret_protected_by_the_api_is_read_by_the_orchestrator` (two service providers over one Testcontainers database) and that the existing webhook secret tests still pass
- [x] 1.5 Split `BatonTelemetry` into the common part, `AddBatonAspNetCoreTelemetry` and `AddBatonNpgsqlTelemetry`, with resource attributes; wire `Baton.Agent` and `Baton.Web` (decision 26); verify `Agent_starts_with_telemetry_and_no_endpoint` and `Resource_names_service_version_and_instance`, and `dotnet build Baton.slnx -c Release` clean

## 2. Audit trail (AUT-11)

- [x] 2.1 `AuditWriter` and actor helper shared with `/api/me` (decisions 12–13); verify `Entry_commits_with_the_change` and `Rejected_change_writes_no_entry` (412 and 400 cases)
- [x] 2.2 Audit every automation endpoint: create, update, delete, clone, enable, disable, rotate-secret, notification channel changes; verify `Automation_update_records_before_and_after`, `Automation_create_has_no_before`, `Deleted_automation_history_stays_readable`, `Secret_rotation_records_no_secret`
- [x] 2.3 Audit host group, agent credential and workflow limit endpoints (decision 3 and "Proposed here"); verify `Group_pause_is_audited_with_its_actor`, `Group_limit_change_records_before_and_after`, `Agent_token_revocation_records_no_token`, `Workflow_limit_set_is_audited`
- [x] 2.4 Redaction check over every audited DTO (decision 14); verify unit test `Audited_dtos_contain_no_secret_values` fails when a `[Secret]` property or a `whsec_` / agent-token value is serialized
- [x] 2.5 `GET /api/audit` with cursor and time range (decision 15); verify `History_of_an_automation_is_newest_first`, `Audit_cursor_pages_without_gaps_or_duplicates`, `Viewer_reads_the_audit_log`
- [x] 2.6 Audit retention in `PartitionMaintenanceLoop` (decision 15); verify Testcontainers test `Audit_entries_older_than_a_year_are_deleted` (366 days deleted, 364 kept) and `Audit_outlives_run_history`

## 3. Re-run failed tasks (WF-10)

- [x] 3.1 `POST /api/runs/{id}/retry-failed` with `RunRerun.RequestAsync` (decision 9); verify `Failed_run_is_rerun_as_pending`, `Timed_out_run_is_rerun_and_its_timeout_cleared`, `Rerun_of_a_run_in_another_state_is_409` (Running, Pending, Succeeded, Cancelled), `Viewer_cannot_rerun`
- [x] 3.2 Re-arming at admission with `RerunPlan` (decision 10); verify Testcontainers tests `Rerun_queues_the_failed_task_with_kept_upstream_outputs`, `Succeeded_tasks_are_not_run_again`, `Rerun_attempts_continue_with_a_fresh_budget`, `Rerun_keeps_the_idempotency_key`, `Rerun_uses_the_original_definition_version`, `Rerun_waits_for_the_automation_run_limit`, `Pinned_rerun_pins_again`
- [x] 3.3 Run outcome after a re-run; verify `Rerun_that_succeeds_ends_succeeded_and_releases_limits` and `Rerun_that_fails_again_can_be_rerun_again`
- [x] 3.4 Concurrency (decision 11); verify concurrent-actor tests `Two_simultaneous_reruns_give_one_202_and_one_409`, `Rerun_admitted_during_claims_leases_each_reset_root_once`, `Rerun_racing_the_reaper_is_rejected_or_rearms_after_it`
- [x] 3.5 Run detail returns `reruns`, attempts of all runs shown (spec "Run detail"); verify `Rerun_run_detail_shows_count_and_all_attempts`; document re-runs and the idempotency key meaning in the `TaskContext.IdempotencyKey` XML docs and `docs/runbooks/rerun-failed-runs.md`

## 4. Queue statistics (OBS-04)

- [ ] 4.1 `QueueStats.ReadAsync` and `GET /api/queue/stats` (decision 24); verify Testcontainers tests `Queue_stats_show_queued_oldest_wait_and_active_leases`, `Lease_expirations_count_the_last_hour_only`, `Idle_group_is_listed_with_zeros`; record `EXPLAIN (ANALYZE)` over the G1 load data in `docs/reports/m7-queue-stats.md`
- [ ] 4.2 Lease expirations per group from the reaper, `QueueMetricsLoop` and the gauges (decision 30); verify `Expired_lease_increments_the_group_counter` and `Oldest_wait_gauge_reports_within_30_s` with a `MeterListener`

## 5. Notifications (OBS-05)

- [ ] 5.1 ADR-0018 and ADR-0019 accepted by the owner; `MailKit` in `Directory.Packages.props`; `mailpit` service in `docker-compose.yml` and SMTP settings in `appsettings.Development.json`; verify `docker compose up -d` starts Mailpit on 1025/8025 and the build is clean
- [ ] 5.2 Notification channels on the automation API, write-only URL, one-time signing secret, rotate-secret, clone drops webhook channels (decision 16); verify `Email_channel_round_trips`, `Invalid_channel_is_rejected_with_a_field_message`, `Read_shows_only_the_webhook_host`, `Update_without_url_keeps_the_stored_url`, `Clone_drops_webhook_channels`
- [ ] 5.3 Enqueue in `RunFinalizer` (decision 18); verify Testcontainers tests `Failed_run_schedules_one_delivery_per_matching_channel`, `Timed_out_run_notifies_channels_on_timed_out`, `Succeeded_and_cancelled_runs_schedule_nothing`, `Deleted_automation_schedules_nothing`, `Rolled_back_finish_schedules_nothing`
- [ ] 5.4 `NotificationLoop` with lease-style claiming and retries (decision 19); verify `Delivery_retries_after_503_and_is_recorded_sent`, `Delivery_fails_after_5_attempts_and_the_run_stays_failed` (OQ-08), `Non_retryable_4xx_fails_at_once`, `Crashed_sender_delivery_is_retried_after_the_lock_expires`, and the concurrent-actor test `Two_loops_send_each_delivery_once`
- [ ] 5.5 Webhook sender (decisions 20–21); verify against an in-process receiver `Webhook_signature_verifies_with_the_channel_secret`, `Retry_carries_the_same_delivery_id`, `Redirect_is_not_followed`, `Body_lists_failed_tasks_and_console_link`
- [ ] 5.6 Email sender (decisions 20, 22); verify against a Mailpit Testcontainer `Email_names_automation_outcome_and_failed_tasks` and `Unconfigured_smtp_fails_email_deliveries_at_once`
- [ ] 5.7 Notification metrics and run events (decision 30); verify `Sent_and_failed_notifications_are_counted_by_kind` and that the run timeline shows `notification.sent` / `notification.failed`; add the webhook allowlist and key ring settings to the deployment runbook

## 6. Telemetry (OBS-06)

- [ ] 6.1 `workflow_runs.trace_parent` written by `RunCreator`, `baton.trigger.fire` activity in `TriggerLoop`, lease query returns it, `TaskLease.trace_parent = 10` in `agent.proto` (decision 27); verify `Run_now_run_carries_the_request_trace`, `Cron_run_carries_the_trigger_trace`, and that `Old_agent_lease_without_trace_parent_still_runs` passes
- [ ] 6.2 Handler span in `TaskExecutor` (decision 28); verify agent tests `Handler_attempt_is_a_span_in_the_run_trace`, `Failed_attempt_span_has_error_status_and_exception`, `Two_tasks_on_two_agents_share_one_trace` (integration)
- [ ] 6.3 Handler log scope and export (decision 29); verify `Handler_log_record_carries_run_task_attempt_and_span` with a capturing logger provider, and that the existing OBS-03 log shipping tests still pass

## 7. Console

- [ ] 7.1 Queue page `/queue` with 5 s refresh (decision 25); verify bUnit tests `Queue_page_lists_groups_with_backlog` and `Queue_page_refreshes_without_reload`
- [ ] 7.2 Re-run failed tasks on the run detail page, re-run count shown, role-aware; verify bUnit tests `Operator_reruns_a_failed_run`, `Succeeded_run_offers_no_rerun`, `Viewer_sees_no_rerun_action`
- [ ] 7.3 Notifications section in the automation editor, write-only URL, secret shown once; verify bUnit tests `Webhook_channel_secret_is_shown_once_after_save`, `Reopened_channel_shows_only_the_host`, `Channel_error_is_shown_next_to_the_channel`
- [ ] 7.4 History view for automations and host groups with a before/after field diff; verify bUnit tests `Automation_history_shows_changed_fields` and `Group_history_shows_the_pause`

## 8. Close

- [ ] 8.1 `dotnet build Baton.slnx -c Release` clean; `dotnet test` green for Domain, Agent, Web and Integration; `npx @fission-ai/openspec@1.14.1 validate --all --strict` green

## Workflow follow-up

- Open the PR linked to the GitHub milestone "M7 — Operability & insight", every exit criterion ticked except the archive. Call out the additive `agent.proto` field and the re-run idempotency note for workflow authors.
- On the owner's OK: `openspec archive add-operability-and-insight`, tick the archive criterion, mark M7 done in `ROADMAP.md` and `README.md`, close the GitHub milestone, merge.
