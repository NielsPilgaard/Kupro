using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimeRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "time_registration",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "time_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    via = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_time_entries_issues_workspace_id_issue_id",
                        columns: x => new { x.workspace_id, x.issue_id },
                        principalTable: "issues",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_time_entries_members_workspace_id_member_id",
                        columns: x => new { x.workspace_id, x.member_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "time_entry_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    via = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    old_date = table.Column<DateOnly>(type: "date", nullable: true),
                    new_date = table.Column<DateOnly>(type: "date", nullable: true),
                    old_minutes = table.Column<int>(type: "integer", nullable: true),
                    new_minutes = table.Column<int>(type: "integer", nullable: true),
                    old_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    new_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_time_entry_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_time_entry_changes_issues_workspace_id_issue_id",
                        columns: x => new { x.workspace_id, x.issue_id },
                        principalTable: "issues",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_time_entry_changes_members_workspace_id_changed_by_id",
                        columns: x => new { x.workspace_id, x.changed_by_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_time_entry_changes_members_workspace_id_member_id",
                        columns: x => new { x.workspace_id, x.member_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_time_entries_workspace_id_date",
                table: "time_entries",
                columns: new[] { "workspace_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entries_workspace_id_issue_id",
                table: "time_entries",
                columns: new[] { "workspace_id", "issue_id" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entries_workspace_id_member_id_date",
                table: "time_entries",
                columns: new[] { "workspace_id", "member_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entry_changes_workspace_id_changed_by_id",
                table: "time_entry_changes",
                columns: new[] { "workspace_id", "changed_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entry_changes_workspace_id_entry_id",
                table: "time_entry_changes",
                columns: new[] { "workspace_id", "entry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entry_changes_workspace_id_issue_id",
                table: "time_entry_changes",
                columns: new[] { "workspace_id", "issue_id" });

            migrationBuilder.CreateIndex(
                name: "ix_time_entry_changes_workspace_id_member_id_at",
                table: "time_entry_changes",
                columns: new[] { "workspace_id", "member_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "time_entries");

            migrationBuilder.DropTable(
                name: "time_entry_changes");

            migrationBuilder.DropColumn(
                name: "time_registration",
                table: "workspaces");
        }
    }
}
