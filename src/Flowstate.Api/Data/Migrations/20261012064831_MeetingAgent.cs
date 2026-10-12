using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowstate.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MeetingAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "proposal_id",
                table: "comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "proposal_id",
                table: "activity",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "meeting_settings",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    full_auto_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    full_auto_operations = table.Column<string[]>(type: "text[]", nullable: false),
                    transcript_retention_days = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meeting_settings", x => x.workspace_id);
                    table.ForeignKey(
                        name: "fk_meeting_settings_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meetings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meetings", x => x.id);
                    table.ForeignKey(
                        name: "fk_meetings_members_agent_id",
                        column: x => x.agent_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meetings_members_started_by_id",
                        column: x => x.started_by_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meetings_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transcript_segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    speaker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_segments", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcript_segments_meetings_session_id",
                        column: x => x.session_id,
                        principalTable: "meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meeting_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidates = table.Column<List<string>>(type: "text[]", nullable: false),
                    operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    before = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    snippet = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    edited = table.Column<bool>(type: "boolean", nullable: false),
                    auto_applied = table.Column<bool>(type: "boolean", nullable: false),
                    reviewed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meeting_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_meeting_proposals_issues_issue_id",
                        column: x => x.issue_id,
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_meeting_proposals_meetings_session_id",
                        column: x => x.session_id,
                        principalTable: "meetings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_meeting_proposals_transcript_segments_segment_id",
                        column: x => x.segment_id,
                        principalTable: "transcript_segments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comments_proposal_id",
                table: "comments",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_proposal_id",
                table: "activity",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_issue_id",
                table: "meeting_proposals",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_segment_id",
                table: "meeting_proposals",
                column: "segment_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_session_id_created_at",
                table: "meeting_proposals",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_workspace_id_operation",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "operation" });

            migrationBuilder.CreateIndex(
                name: "ix_meetings_agent_id",
                table: "meetings",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_meetings_project_id",
                table: "meetings",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_meetings_started_by_id",
                table: "meetings",
                column: "started_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_meetings_workspace_id_started_at",
                table: "meetings",
                columns: new[] { "workspace_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_segments_created_at",
                table: "transcript_segments",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_transcript_segments_session_id_created_at",
                table: "transcript_segments",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_activity_meeting_proposals_proposal_id",
                table: "activity",
                column: "proposal_id",
                principalTable: "meeting_proposals",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_meeting_proposals_proposal_id",
                table: "comments",
                column: "proposal_id",
                principalTable: "meeting_proposals",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_activity_meeting_proposals_proposal_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_meeting_proposals_proposal_id",
                table: "comments");

            migrationBuilder.DropTable(
                name: "meeting_proposals");

            migrationBuilder.DropTable(
                name: "meeting_settings");

            migrationBuilder.DropTable(
                name: "transcript_segments");

            migrationBuilder.DropTable(
                name: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_comments_proposal_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_activity_proposal_id",
                table: "activity");

            migrationBuilder.DropColumn(
                name: "proposal_id",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "proposal_id",
                table: "activity");
        }
    }
}
