using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "credential_id",
                table: "hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "drain_requested",
                table: "hosts",
                type: "boolean",
                nullable: false,
                defaultValueSql: "false");

            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("agent_credentials_pkey", x => x.id);
                    table.ForeignKey(
                        name: "agent_credentials_host_group_id_fkey",
                        column: x => x.host_group_id,
                        principalTable: "host_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "agent_credentials_host_group_id_name_key",
                table: "agent_credentials",
                columns: new[] { "host_group_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "agent_credentials_token_hash_key",
                table: "agent_credentials",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "hosts_credential_id_fkey",
                table: "hosts",
                column: "credential_id",
                principalTable: "agent_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "hosts_credential_id_fkey",
                table: "hosts");

            migrationBuilder.DropTable(
                name: "agent_credentials");

            migrationBuilder.DropColumn(
                name: "credential_id",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "drain_requested",
                table: "hosts");
        }
    }
}
