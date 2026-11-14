using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <summary>
    /// M6 (add-scheduling-and-routing-controls): the CatchUp limit on cron triggers (AUT-08), the pin on the
    /// run (ORC-12, design.md decision 11) and the <c>pinned_host_lost</c> cancel reason (decision 13). Run and
    /// task columns are hand-written (EF does not model the run tables, ADR-0007). Mirrors
    /// <c>Schema/0006_scheduling_and_routing.sql</c>.
    /// </summary>
    public partial class SchedulingAndRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "catch_up_limit",
                table: "automation_triggers",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "automation_triggers_catch_up_limit_check",
                table: "automation_triggers",
                sql: "catch_up_limit BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "automation_triggers_catch_up_limit_policy_check",
                table: "automation_triggers",
                sql: "(misfire_policy = 'catch_up') = (catch_up_limit IS NOT NULL)");

            migrationBuilder.Sql(RunColumnsUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RunColumnsDown);

            migrationBuilder.DropCheckConstraint(
                name: "automation_triggers_catch_up_limit_check",
                table: "automation_triggers");

            migrationBuilder.DropCheckConstraint(
                name: "automation_triggers_catch_up_limit_policy_check",
                table: "automation_triggers");

            migrationBuilder.DropColumn(
                name: "catch_up_limit",
                table: "automation_triggers");
        }
    
        private const string RunColumnsUp = """
            ALTER TABLE workflow_runs
              ADD COLUMN placement_mode text NOT NULL DEFAULT 'per_task'
                CHECK (placement_mode IN ('per_task','pinned_run')),
              ADD COLUMN pin_labels     text[] NOT NULL DEFAULT '{}';
            CREATE INDEX ix_runs_pinned ON workflow_runs (pinned_host_id) WHERE pinned_host_id IS NOT NULL;
            ALTER TABLE task_runs
              ADD COLUMN pinned_run boolean NOT NULL DEFAULT false,
              DROP COLUMN pinned_host_id;
            ALTER TABLE workflow_runs DROP CONSTRAINT workflow_runs_cancel_reason_check,
              ADD CONSTRAINT workflow_runs_cancel_reason_check
                CHECK (cancel_reason IN ('user','replaced','timeout','pinned_host_lost'));
            """;

        // Runs failed for a lost pinned host are recorded as failed already; only the reason goes back.
        private const string RunColumnsDown = """
            UPDATE workflow_runs SET cancel_reason = 'user' WHERE cancel_reason = 'pinned_host_lost';
            ALTER TABLE workflow_runs DROP CONSTRAINT workflow_runs_cancel_reason_check,
              ADD CONSTRAINT workflow_runs_cancel_reason_check CHECK (cancel_reason IN ('user','replaced','timeout'));
            ALTER TABLE task_runs
              DROP COLUMN pinned_run,
              ADD COLUMN pinned_host_id uuid;
            DROP INDEX ix_runs_pinned;
            ALTER TABLE workflow_runs
              DROP COLUMN placement_mode,
              DROP COLUMN pin_labels;
            """;
    }
}
