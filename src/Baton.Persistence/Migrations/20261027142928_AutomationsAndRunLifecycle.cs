using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <summary>
    /// M4 (add-automations-and-run-lifecycle): soft-deleted automations, encrypted webhook secrets and the
    /// Data Protection key ring (design.md decisions 2 and 5, ADR-0015), plus hand-written run and task
    /// columns (EF does not model the run tables, ADR-0007). Mirrors <c>Schema/0004_automations.sql</c>.
    /// </summary>
    public partial class AutomationsAndRunLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "automations_name_key",
                table: "automations");

            migrationBuilder.RenameColumn(
                name: "webhook_secret_hash",
                table: "automation_triggers",
                newName: "webhook_secret");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "automations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("data_protection_keys_pkey", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "automations_name_key",
                table: "automations",
                column: "name",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.Sql(RunColumnsUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RunColumnsDown);

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropIndex(
                name: "automations_name_key",
                table: "automations");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "automations");

            migrationBuilder.RenameColumn(
                name: "webhook_secret",
                table: "automation_triggers",
                newName: "webhook_secret_hash");

            migrationBuilder.CreateIndex(
                name: "automations_name_key",
                table: "automations",
                column: "name",
                unique: true);
        }

        private const string RunColumnsUp = """
            ALTER TABLE workflow_runs
              ADD COLUMN limit_keys      text[] NOT NULL DEFAULT '{}',
              ADD COLUMN required_labels text[] NOT NULL DEFAULT '{}',
              ADD COLUMN run_timeout     interval,
              ADD COLUMN cancel_reason   text CHECK (cancel_reason IN ('user','replaced','timeout')),
              ADD COLUMN blocked_on      text;
            ALTER TABLE task_runs ADD COLUMN cancel_requested_at timestamptz;
            """;

        private const string RunColumnsDown = """
            ALTER TABLE task_runs DROP COLUMN cancel_requested_at;
            ALTER TABLE workflow_runs
              DROP COLUMN limit_keys,
              DROP COLUMN required_labels,
              DROP COLUMN run_timeout,
              DROP COLUMN cancel_reason,
              DROP COLUMN blocked_on;
            """;
    }
}
