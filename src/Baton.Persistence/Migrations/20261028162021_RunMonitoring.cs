using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <summary>
    /// M5 (add-web-console-and-monitoring): the index behind the newest-first runs list and its cursor
    /// (OBS-01, design.md decision 8). Mirrors <c>Schema/0005_run_monitoring.sql</c>.
    /// </summary>
    public partial class RunMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("CREATE INDEX ix_runs_recent ON workflow_runs (created_at DESC, id DESC);");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP INDEX ix_runs_recent;");
    }
}
