using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowstate.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "sprint_id",
                table: "issues",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    goal = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sprints", x => x.id);
                    table.ForeignKey(
                        name: "fk_sprints_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sprints_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_issues_sprint_id",
                table: "issues",
                column: "sprint_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_sprint_id",
                table: "issues",
                columns: new[] { "workspace_id", "sprint_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sprints_one_active_per_project",
                table: "sprints",
                column: "project_id",
                unique: true,
                filter: "state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_sprints_workspace_id_project_id_start_date",
                table: "sprints",
                columns: new[] { "workspace_id", "project_id", "start_date" });

            migrationBuilder.AddForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues",
                column: "sprint_id",
                principalTable: "sprints",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues");

            migrationBuilder.DropTable(
                name: "sprints");

            migrationBuilder.DropIndex(
                name: "ix_issues_sprint_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_workspace_id_sprint_id",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "sprint_id",
                table: "issues");
        }
    }
}
