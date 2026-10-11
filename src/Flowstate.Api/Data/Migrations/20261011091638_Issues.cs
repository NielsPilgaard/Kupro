using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowstate.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Issues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_idempotency_records_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    description = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    assignee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    effort = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    rank = table.Column<double>(type: "double precision", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issues", x => x.id);
                    table.ForeignKey(
                        name: "fk_issues_issues_parent_id",
                        column: x => x.parent_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_members_assignee_id",
                        column: x => x.assignee_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_members_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_issues_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "labels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_labels", x => x.id);
                    table.ForeignKey(
                        name: "fk_labels_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    via = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_issues_issue_id",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_activity_members_actor_id",
                        column: x => x.actor_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "issue_relations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_relations", x => x.id);
                    table.ForeignKey(
                        name: "fk_issue_relations_issues_source_id",
                        column: x => x.source_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_issue_relations_issues_target_id",
                        column: x => x.target_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issue_labels",
                columns: table => new
                {
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_labels", x => new { x.issue_id, x.label_id });
                    table.ForeignKey(
                        name: "fk_issue_labels_issues_issue_id",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_issue_labels_labels_label_id",
                        column: x => x.label_id,
                        principalTable: "labels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activity_actor_id",
                table: "activity",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_issue_id",
                table: "activity",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_workspace_id_issue_id_created_at",
                table: "activity",
                columns: new[] { "workspace_id", "issue_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_created_at",
                table: "idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_user_id_key",
                table: "idempotency_records",
                columns: new[] { "user_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_labels_label_id",
                table: "issue_labels",
                column: "label_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_labels_workspace_id_label_id",
                table: "issue_labels",
                columns: new[] { "workspace_id", "label_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_relations_source_id_target_id_type",
                table: "issue_relations",
                columns: new[] { "source_id", "target_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_relations_target_id",
                table: "issue_relations",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_relations_workspace_id_target_id",
                table: "issue_relations",
                columns: new[] { "workspace_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_assignee_id",
                table: "issues",
                column: "assignee_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_created_by_id",
                table: "issues",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_parent_id",
                table: "issues",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_issues_project_id_number",
                table: "issues",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_assignee_id",
                table: "issues",
                columns: new[] { "workspace_id", "assignee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_key",
                table: "issues",
                columns: new[] { "workspace_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_parent_id",
                table: "issues",
                columns: new[] { "workspace_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_project_id_state_rank",
                table: "issues",
                columns: new[] { "workspace_id", "project_id", "state", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_type",
                table: "issues",
                columns: new[] { "workspace_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_updated_at_id",
                table: "issues",
                columns: new[] { "workspace_id", "updated_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_labels_workspace_id_name",
                table: "labels",
                columns: new[] { "workspace_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "issue_labels");

            migrationBuilder.DropTable(
                name: "issue_relations");

            migrationBuilder.DropTable(
                name: "labels");

            migrationBuilder.DropTable(
                name: "issues");
        }
    }
}
