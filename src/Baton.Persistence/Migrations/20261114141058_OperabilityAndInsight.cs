using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <summary>
    /// M7 (add-operability-and-insight): the audit log (AUT-11) and notification channels (OBS-05) through EF;
    /// the notification outbox, the re-run counter and trace context on runs (WF-10, OBS-06) and the lost-attempt
    /// index (OBS-04) as hand-written SQL, because EF does not model the run tables (ADR-0007). Mirrors
    /// <c>Schema/0007_operability_and_insight.sql</c>.
    /// </summary>
    public partial class OperabilityAndInsight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    actor_name = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("audit_log_pkey", x => x.id);
                    table.CheckConstraint("audit_log_entity_type_check", "entity_type IN ('automation','host_group','agent_credential','workflow_limit')");
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    email_to = table.Column<string[]>(type: "text[]", nullable: true),
                    webhook_url = table.Column<byte[]>(type: "bytea", nullable: true),
                    webhook_url_display = table.Column<string>(type: "text", nullable: true),
                    webhook_secret = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("notification_channels_pkey", x => x.id);
                    table.CheckConstraint("notification_channels_email_check", "(kind = 'email') = (email_to IS NOT NULL)");
                    table.CheckConstraint("notification_channels_events_check", "events <@ ARRAY['failed','timed_out'] AND cardinality(events) > 0");
                    table.CheckConstraint("notification_channels_kind_check", "kind IN ('email','webhook')");
                    table.CheckConstraint("notification_channels_webhook_check", "(kind = 'webhook') = (webhook_url IS NOT NULL AND webhook_secret IS NOT NULL)");
                    table.ForeignKey(
                        name: "notification_channels_automation_id_fkey",
                        column: x => x.automation_id,
                        principalTable: "automations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id", "at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_notification_channels_automation",
                table: "notification_channels",
                column: "automation_id");

            migrationBuilder.Sql(RunTablesUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RunTablesDown);

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "notification_channels");
        }

        private const string RunTablesUp = """
            CREATE TABLE notification_deliveries (
              id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
              workflow_run_id uuid NOT NULL REFERENCES workflow_runs(id) ON DELETE CASCADE,
              channel_id      uuid NOT NULL REFERENCES notification_channels(id) ON DELETE CASCADE,
              event           text NOT NULL CHECK (event IN ('failed','timed_out')),
              status          text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','sent','failed')),
              attempts        int NOT NULL DEFAULT 0,
              next_attempt_at timestamptz NOT NULL DEFAULT now(),
              locked_until    timestamptz,
              last_error      text,
              created_at      timestamptz NOT NULL DEFAULT now(),
              sent_at         timestamptz
            );
            CREATE INDEX ix_deliveries_due ON notification_deliveries (next_attempt_at) WHERE status = 'pending';
            CREATE INDEX ix_deliveries_run ON notification_deliveries (workflow_run_id);
            CREATE INDEX ix_deliveries_channel ON notification_deliveries (channel_id);

            ALTER TABLE workflow_runs
              ADD COLUMN reruns       int NOT NULL DEFAULT 0,
              ADD COLUMN trace_parent text;

            CREATE INDEX ix_attempts_lost ON task_attempts (finished_at) WHERE outcome = 'lost';
            """;

        private const string RunTablesDown = """
            DROP INDEX ix_attempts_lost;
            ALTER TABLE workflow_runs DROP COLUMN trace_parent, DROP COLUMN reruns;
            DROP TABLE notification_deliveries;
            """;
    }
}
