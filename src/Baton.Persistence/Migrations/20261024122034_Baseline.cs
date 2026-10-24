using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "concurrency_limits",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    max = table.Column<int>(type: "integer", nullable: false),
                    in_use = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("concurrency_limits_pkey", x => x.key);
                    table.CheckConstraint("concurrency_limits_in_use_check", "in_use >= 0");
                });

            migrationBuilder.CreateTable(
                name: "host_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    max_concurrent_runs = table.Column<int>(type: "integer", nullable: true),
                    max_concurrent_tasks = table.Column<int>(type: "integer", nullable: true),
                    lease_seconds = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "60"),
                    dispatch_paused = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false")
                },
                constraints: table =>
                {
                    table.PrimaryKey("host_groups_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    graph_hash = table.Column<string>(type: "character(64)", nullable: false),
                    graph = table.Column<string>(type: "jsonb", nullable: false),
                    params_schema = table.Column<string>(type: "jsonb", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    is_retired = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "false")
                },
                constraints: table =>
                {
                    table.PrimaryKey("workflow_definitions_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true"),
                    workflow_key = table.Column<string>(type: "text", nullable: false),
                    workflow_version = table.Column<int>(type: "integer", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    host_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    required_labels = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValueSql: "50"),
                    max_concurrent_runs = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "1"),
                    concurrency_policy = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'skip'"),
                    placement_mode = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'per_task'"),
                    run_timeout = table.Column<TimeSpan>(type: "interval", nullable: true),
                    owner = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    row_version = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("automations_pkey", x => x.id);
                    table.CheckConstraint("automations_concurrency_policy_check", "concurrency_policy IN ('skip','queue','replace','allow')");
                    table.CheckConstraint("automations_placement_mode_check", "placement_mode IN ('per_task','pinned_run')");
                    table.CheckConstraint("automations_priority_check", "priority BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "automations_host_group_id_fkey",
                        column: x => x.host_group_id,
                        principalTable: "host_groups",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    host_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    labels = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    capacity_slots = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    definition_hashes = table.Column<string[]>(type: "text[]", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("hosts_pkey", x => x.id);
                    table.CheckConstraint("hosts_capacity_slots_check", "capacity_slots > 0");
                    table.CheckConstraint("hosts_status_check", "status IN ('online','draining','offline')");
                    table.ForeignKey(
                        name: "hosts_host_group_id_fkey",
                        column: x => x.host_group_id,
                        principalTable: "host_groups",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "automation_triggers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    cron_expr = table.Column<string>(type: "text", nullable: true),
                    time_zone = table.Column<string>(type: "text", nullable: true),
                    misfire_policy = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'fire_once'"),
                    next_fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    webhook_secret_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValueSql: "true")
                },
                constraints: table =>
                {
                    table.PrimaryKey("automation_triggers_pkey", x => x.id);
                    table.CheckConstraint("automation_triggers_kind_check", "kind IN ('cron','webhook','manual')");
                    table.CheckConstraint("automation_triggers_misfire_policy_check", "misfire_policy IN ('fire_once','skip','catch_up')");
                    table.ForeignKey(
                        name: "automation_triggers_automation_id_fkey",
                        column: x => x.automation_id,
                        principalTable: "automations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_triggers_due",
                table: "automation_triggers",
                column: "next_fire_at",
                filter: "enabled AND kind = 'cron'");

            migrationBuilder.CreateIndex(
                name: "automations_name_key",
                table: "automations",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "host_groups_name_key",
                table: "host_groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "hosts_name_key",
                table: "hosts",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "workflow_definitions_key_version_key",
                table: "workflow_definitions",
                columns: new[] { "key", "version" },
                unique: true);

            migrationBuilder.Sql(RuntimeTablesUp);
            migrationBuilder.Sql(PartitionMaintenanceUp);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RuntimeTablesDown);

            migrationBuilder.DropTable(
                name: "automation_triggers");

            migrationBuilder.DropTable(
                name: "concurrency_limits");

            migrationBuilder.DropTable(
                name: "hosts");

            migrationBuilder.DropTable(
                name: "workflow_definitions");

            migrationBuilder.DropTable(
                name: "automations");

            migrationBuilder.DropTable(
                name: "host_groups");
        }
    }
}
