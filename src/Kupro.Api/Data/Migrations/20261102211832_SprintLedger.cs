using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SprintLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sprint_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_delta = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    remaining_delta = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprint_ledger", x => x.id);
                    table.ForeignKey(
                        name: "fk_sprint_ledger_issues_workspace_id_issue_id",
                        columns: x => new { x.workspace_id, x.issue_id },
                        principalTable: "issues",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sprint_ledger_members_workspace_id_assignee_id",
                        columns: x => new { x.workspace_id, x.assignee_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprint_ledger_sprints_workspace_id_sprint_id",
                        columns: x => new { x.workspace_id, x.sprint_id },
                        principalTable: "sprints",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_ledger_workspace_id_assignee_id",
                table: "sprint_ledger",
                columns: new[] { "workspace_id", "assignee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_ledger_workspace_id_issue_id",
                table: "sprint_ledger",
                columns: new[] { "workspace_id", "issue_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_ledger_workspace_id_sprint_id_at",
                table: "sprint_ledger",
                columns: new[] { "workspace_id", "sprint_id", "at" });

            // ADR-020 IMP-003: active sprints get their baseline now, so their burndown starts today. States 4 and 5 are Done and Canceled.
            migrationBuilder.Sql("""
                INSERT INTO sprint_ledger (id, workspace_id, sprint_id, issue_id, assignee_id, at, kind, scope_delta, remaining_delta)
                SELECT gen_random_uuid(), i.workspace_id, i.sprint_id, i.id, i.assignee_id, now(), 'Baseline',
                       CASE WHEN i.state = 5 THEN 0 ELSE coalesce(i.effort, 0) END,
                       CASE WHEN i.state IN (4, 5) THEN 0 ELSE coalesce(i.effort, 0) END
                FROM issues i
                JOIN sprints s ON s.id = i.sprint_id
                WHERE s.state = 'Active';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sprint_ledger");
        }
    }
}
