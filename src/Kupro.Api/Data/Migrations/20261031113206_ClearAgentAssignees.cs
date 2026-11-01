using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearAgentAssignees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-008: the assignee answers for the work, so agents can no longer hold it. Existing agent assignees are dropped.
            migrationBuilder.Sql("""
                UPDATE issues SET assignee_id = NULL
                WHERE assignee_id IN (SELECT id FROM members WHERE kind = 'Agent');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only; the previous assignees aren't kept.
        }
    }
}
