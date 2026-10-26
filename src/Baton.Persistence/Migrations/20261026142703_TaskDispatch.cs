using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Baton.Persistence.Migrations
{
    /// <summary>
    /// M3 (add-task-dispatch): <c>task_runs.leased_at</c>, the claim time of the current attempt
    /// (design.md "Implementation notes"). Hand-written: EF does not model the run tables (ADR-0007).
    /// Mirrors <c>Schema/0003_task_dispatch.sql</c>.
    /// </summary>
    public partial class TaskDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE task_runs ADD COLUMN leased_at timestamptz;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE task_runs DROP COLUMN leased_at;");
        }
    }
}
