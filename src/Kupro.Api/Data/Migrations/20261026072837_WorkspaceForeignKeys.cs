using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_activity_issues_issue_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_activity_meeting_proposals_proposal_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_activity_members_actor_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_attachments_issues_issue_id",
                table: "attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_attachments_members_uploaded_by_id",
                table: "attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_issues_issue_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_meeting_proposals_proposal_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_members_author_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_labels_issues_issue_id",
                table: "issue_labels");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_labels_labels_label_id",
                table: "issue_labels");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_relations_issues_source_id",
                table: "issue_relations");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_relations_issues_target_id",
                table: "issue_relations");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_issues_parent_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_members_assignee_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_members_created_by_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_projects_project_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_issues_issue_id",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_meetings_session_id",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_transcript_segments_segment_id",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_members_agent_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_members_started_by_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_projects_project_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_milestones_projects_project_id",
                table: "milestones");

            migrationBuilder.DropForeignKey(
                name: "fk_project_capacity_projects_project_id",
                table: "project_capacity");

            migrationBuilder.DropForeignKey(
                name: "fk_sprints_projects_project_id",
                table: "sprints");

            migrationBuilder.DropForeignKey(
                name: "fk_transcript_segments_meetings_session_id",
                table: "transcript_segments");

            migrationBuilder.DropForeignKey(
                name: "fk_workspace_exports_members_requested_by_id",
                table: "workspace_exports");

            migrationBuilder.DropIndex(
                name: "ix_workspace_exports_requested_by_id",
                table: "workspace_exports");

            migrationBuilder.DropIndex(
                name: "ix_milestones_project_id",
                table: "milestones");

            migrationBuilder.DropIndex(
                name: "ix_meetings_agent_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meetings_project_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meetings_started_by_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meeting_proposals_issue_id",
                table: "meeting_proposals");

            migrationBuilder.DropIndex(
                name: "ix_meeting_proposals_segment_id",
                table: "meeting_proposals");

            migrationBuilder.DropIndex(
                name: "ix_issues_assignee_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_created_by_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_parent_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_sprint_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issue_relations_target_id",
                table: "issue_relations");

            migrationBuilder.DropIndex(
                name: "ix_issue_labels_label_id",
                table: "issue_labels");

            migrationBuilder.DropIndex(
                name: "ix_comments_author_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_comments_issue_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_comments_proposal_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_issue_id",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_uploaded_by_id",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_activity_actor_id",
                table: "activity");

            migrationBuilder.DropIndex(
                name: "ix_activity_issue_id",
                table: "activity");

            migrationBuilder.DropIndex(
                name: "ix_activity_proposal_id",
                table: "activity");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_transcript_segments_workspace_id_id",
                table: "transcript_segments",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_sprints_workspace_id_id",
                table: "sprints",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_projects_workspace_id_id",
                table: "projects",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_members_workspace_id_id",
                table: "members",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_meetings_workspace_id_id",
                table: "meetings",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_meeting_proposals_workspace_id_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_labels_workspace_id_id",
                table: "labels",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_issues_workspace_id_id",
                table: "issues",
                columns: new[] { "workspace_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_exports_workspace_id_requested_by_id",
                table: "workspace_exports",
                columns: new[] { "workspace_id", "requested_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_segments_workspace_id_session_id",
                table: "transcript_segments",
                columns: new[] { "workspace_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_milestones_workspace_id_project_id",
                table: "milestones",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meetings_workspace_id_agent_id",
                table: "meetings",
                columns: new[] { "workspace_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meetings_workspace_id_project_id",
                table: "meetings",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meetings_workspace_id_started_by_id",
                table: "meetings",
                columns: new[] { "workspace_id", "started_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_workspace_id_issue_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "issue_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_workspace_id_segment_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "segment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_workspace_id_session_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issues_workspace_id_created_by_id",
                table: "issues",
                columns: new[] { "workspace_id", "created_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_relations_workspace_id_source_id",
                table: "issue_relations",
                columns: new[] { "workspace_id", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_labels_workspace_id_issue_id",
                table: "issue_labels",
                columns: new[] { "workspace_id", "issue_id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_workspace_id_author_id",
                table: "comments",
                columns: new[] { "workspace_id", "author_id" });

            migrationBuilder.CreateIndex(
                name: "ix_comments_workspace_id_proposal_id",
                table: "comments",
                columns: new[] { "workspace_id", "proposal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_workspace_id_uploaded_by_id",
                table: "attachments",
                columns: new[] { "workspace_id", "uploaded_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_workspace_id_actor_id",
                table: "activity",
                columns: new[] { "workspace_id", "actor_id" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_workspace_id_proposal_id",
                table: "activity",
                columns: new[] { "workspace_id", "proposal_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_activity_issues_workspace_id_issue_id",
                table: "activity",
                columns: new[] { "workspace_id", "issue_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            // SET NULL on the whole composite key would null workspace_id too; null only proposal_id (PG 15+).
            migrationBuilder.Sql("""
                ALTER TABLE activity ADD CONSTRAINT fk_activity_meeting_proposals_workspace_id_proposal_id
                FOREIGN KEY (workspace_id, proposal_id) REFERENCES meeting_proposals (workspace_id, id) ON DELETE SET NULL (proposal_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_activity_members_workspace_id_actor_id",
                table: "activity",
                columns: new[] { "workspace_id", "actor_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // SET NULL on the whole composite key would null workspace_id too; null only issue_id (PG 15+).
            migrationBuilder.Sql("""
                ALTER TABLE attachments ADD CONSTRAINT fk_attachments_issues_workspace_id_issue_id
                FOREIGN KEY (workspace_id, issue_id) REFERENCES issues (workspace_id, id) ON DELETE SET NULL (issue_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_attachments_members_workspace_id_uploaded_by_id",
                table: "attachments",
                columns: new[] { "workspace_id", "uploaded_by_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_issues_workspace_id_issue_id",
                table: "comments",
                columns: new[] { "workspace_id", "issue_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            // SET NULL on the whole composite key would null workspace_id too; null only proposal_id (PG 15+).
            migrationBuilder.Sql("""
                ALTER TABLE comments ADD CONSTRAINT fk_comments_meeting_proposals_workspace_id_proposal_id
                FOREIGN KEY (workspace_id, proposal_id) REFERENCES meeting_proposals (workspace_id, id) ON DELETE SET NULL (proposal_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_members_workspace_id_author_id",
                table: "comments",
                columns: new[] { "workspace_id", "author_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_labels_issues_workspace_id_issue_id",
                table: "issue_labels",
                columns: new[] { "workspace_id", "issue_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_labels_labels_workspace_id_label_id",
                table: "issue_labels",
                columns: new[] { "workspace_id", "label_id" },
                principalTable: "labels",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_relations_issues_workspace_id_source_id",
                table: "issue_relations",
                columns: new[] { "workspace_id", "source_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_relations_issues_workspace_id_target_id",
                table: "issue_relations",
                columns: new[] { "workspace_id", "target_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_issues_workspace_id_parent_id",
                table: "issues",
                columns: new[] { "workspace_id", "parent_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_members_workspace_id_assignee_id",
                table: "issues",
                columns: new[] { "workspace_id", "assignee_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_members_workspace_id_created_by_id",
                table: "issues",
                columns: new[] { "workspace_id", "created_by_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_projects_workspace_id_project_id",
                table: "issues",
                columns: new[] { "workspace_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_sprints_workspace_id_sprint_id",
                table: "issues",
                columns: new[] { "workspace_id", "sprint_id" },
                principalTable: "sprints",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meeting_proposals_issues_workspace_id_issue_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "issue_id" },
                principalTable: "issues",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_meeting_proposals_meetings_workspace_id_session_id",
                table: "meeting_proposals",
                columns: new[] { "workspace_id", "session_id" },
                principalTable: "meetings",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            // SET NULL on the whole composite key would null workspace_id too; null only segment_id (PG 15+).
            migrationBuilder.Sql("""
                ALTER TABLE meeting_proposals ADD CONSTRAINT fk_meeting_proposals_transcript_segments_workspace_id_segment_
                FOREIGN KEY (workspace_id, segment_id) REFERENCES transcript_segments (workspace_id, id) ON DELETE SET NULL (segment_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_members_workspace_id_agent_id",
                table: "meetings",
                columns: new[] { "workspace_id", "agent_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_members_workspace_id_started_by_id",
                table: "meetings",
                columns: new[] { "workspace_id", "started_by_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_projects_workspace_id_project_id",
                table: "meetings",
                columns: new[] { "workspace_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_milestones_projects_workspace_id_project_id",
                table: "milestones",
                columns: new[] { "workspace_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_project_capacity_projects_workspace_id_project_id",
                table: "project_capacity",
                columns: new[] { "workspace_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_sprints_projects_workspace_id_project_id",
                table: "sprints",
                columns: new[] { "workspace_id", "project_id" },
                principalTable: "projects",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_transcript_segments_meetings_workspace_id_session_id",
                table: "transcript_segments",
                columns: new[] { "workspace_id", "session_id" },
                principalTable: "meetings",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_workspace_exports_members_workspace_id_requested_by_id",
                table: "workspace_exports",
                columns: new[] { "workspace_id", "requested_by_id" },
                principalTable: "members",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_activity_issues_workspace_id_issue_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_activity_meeting_proposals_workspace_id_proposal_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_activity_members_workspace_id_actor_id",
                table: "activity");

            migrationBuilder.DropForeignKey(
                name: "fk_attachments_issues_workspace_id_issue_id",
                table: "attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_attachments_members_workspace_id_uploaded_by_id",
                table: "attachments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_issues_workspace_id_issue_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_meeting_proposals_workspace_id_proposal_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_comments_members_workspace_id_author_id",
                table: "comments");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_labels_issues_workspace_id_issue_id",
                table: "issue_labels");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_labels_labels_workspace_id_label_id",
                table: "issue_labels");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_relations_issues_workspace_id_source_id",
                table: "issue_relations");

            migrationBuilder.DropForeignKey(
                name: "fk_issue_relations_issues_workspace_id_target_id",
                table: "issue_relations");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_issues_workspace_id_parent_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_members_workspace_id_assignee_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_members_workspace_id_created_by_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_projects_workspace_id_project_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_issues_sprints_workspace_id_sprint_id",
                table: "issues");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_issues_workspace_id_issue_id",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_meetings_workspace_id_session_id",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meeting_proposals_transcript_segments_workspace_id_segment_",
                table: "meeting_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_members_workspace_id_agent_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_members_workspace_id_started_by_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_meetings_projects_workspace_id_project_id",
                table: "meetings");

            migrationBuilder.DropForeignKey(
                name: "fk_milestones_projects_workspace_id_project_id",
                table: "milestones");

            migrationBuilder.DropForeignKey(
                name: "fk_project_capacity_projects_workspace_id_project_id",
                table: "project_capacity");

            migrationBuilder.DropForeignKey(
                name: "fk_sprints_projects_workspace_id_project_id",
                table: "sprints");

            migrationBuilder.DropForeignKey(
                name: "fk_transcript_segments_meetings_workspace_id_session_id",
                table: "transcript_segments");

            migrationBuilder.DropForeignKey(
                name: "fk_workspace_exports_members_workspace_id_requested_by_id",
                table: "workspace_exports");

            migrationBuilder.DropIndex(
                name: "ix_workspace_exports_workspace_id_requested_by_id",
                table: "workspace_exports");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_transcript_segments_workspace_id_id",
                table: "transcript_segments");

            migrationBuilder.DropIndex(
                name: "ix_transcript_segments_workspace_id_session_id",
                table: "transcript_segments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_sprints_workspace_id_id",
                table: "sprints");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_projects_workspace_id_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_milestones_workspace_id_project_id",
                table: "milestones");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_members_workspace_id_id",
                table: "members");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_meetings_workspace_id_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meetings_workspace_id_agent_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meetings_workspace_id_project_id",
                table: "meetings");

            migrationBuilder.DropIndex(
                name: "ix_meetings_workspace_id_started_by_id",
                table: "meetings");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_meeting_proposals_workspace_id_id",
                table: "meeting_proposals");

            migrationBuilder.DropIndex(
                name: "ix_meeting_proposals_workspace_id_issue_id",
                table: "meeting_proposals");

            migrationBuilder.DropIndex(
                name: "ix_meeting_proposals_workspace_id_segment_id",
                table: "meeting_proposals");

            migrationBuilder.DropIndex(
                name: "ix_meeting_proposals_workspace_id_session_id",
                table: "meeting_proposals");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_labels_workspace_id_id",
                table: "labels");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_issues_workspace_id_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issues_workspace_id_created_by_id",
                table: "issues");

            migrationBuilder.DropIndex(
                name: "ix_issue_relations_workspace_id_source_id",
                table: "issue_relations");

            migrationBuilder.DropIndex(
                name: "ix_issue_labels_workspace_id_issue_id",
                table: "issue_labels");

            migrationBuilder.DropIndex(
                name: "ix_comments_workspace_id_author_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_comments_workspace_id_proposal_id",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "ix_attachments_workspace_id_uploaded_by_id",
                table: "attachments");

            migrationBuilder.DropIndex(
                name: "ix_activity_workspace_id_actor_id",
                table: "activity");

            migrationBuilder.DropIndex(
                name: "ix_activity_workspace_id_proposal_id",
                table: "activity");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_exports_requested_by_id",
                table: "workspace_exports",
                column: "requested_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_milestones_project_id",
                table: "milestones",
                column: "project_id");

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
                name: "ix_meeting_proposals_issue_id",
                table: "meeting_proposals",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_meeting_proposals_segment_id",
                table: "meeting_proposals",
                column: "segment_id");

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
                name: "ix_issues_sprint_id",
                table: "issues",
                column: "sprint_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_relations_target_id",
                table: "issue_relations",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_issue_labels_label_id",
                table: "issue_labels",
                column: "label_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_author_id",
                table: "comments",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_issue_id",
                table: "comments",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_comments_proposal_id",
                table: "comments",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_issue_id",
                table: "attachments",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_uploaded_by_id",
                table: "attachments",
                column: "uploaded_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_actor_id",
                table: "activity",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_issue_id",
                table: "activity",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_proposal_id",
                table: "activity",
                column: "proposal_id");

            migrationBuilder.AddForeignKey(
                name: "fk_activity_issues_issue_id",
                table: "activity",
                column: "issue_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_activity_meeting_proposals_proposal_id",
                table: "activity",
                column: "proposal_id",
                principalTable: "meeting_proposals",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_activity_members_actor_id",
                table: "activity",
                column: "actor_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_attachments_issues_issue_id",
                table: "attachments",
                column: "issue_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_attachments_members_uploaded_by_id",
                table: "attachments",
                column: "uploaded_by_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_issues_issue_id",
                table: "comments",
                column: "issue_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_meeting_proposals_proposal_id",
                table: "comments",
                column: "proposal_id",
                principalTable: "meeting_proposals",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_comments_members_author_id",
                table: "comments",
                column: "author_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_labels_issues_issue_id",
                table: "issue_labels",
                column: "issue_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_labels_labels_label_id",
                table: "issue_labels",
                column: "label_id",
                principalTable: "labels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_relations_issues_source_id",
                table: "issue_relations",
                column: "source_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issue_relations_issues_target_id",
                table: "issue_relations",
                column: "target_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_issues_parent_id",
                table: "issues",
                column: "parent_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_members_assignee_id",
                table: "issues",
                column: "assignee_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_members_created_by_id",
                table: "issues",
                column: "created_by_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_projects_project_id",
                table: "issues",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_issues_sprints_sprint_id",
                table: "issues",
                column: "sprint_id",
                principalTable: "sprints",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meeting_proposals_issues_issue_id",
                table: "meeting_proposals",
                column: "issue_id",
                principalTable: "issues",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_meeting_proposals_meetings_session_id",
                table: "meeting_proposals",
                column: "session_id",
                principalTable: "meetings",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_meeting_proposals_transcript_segments_segment_id",
                table: "meeting_proposals",
                column: "segment_id",
                principalTable: "transcript_segments",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_members_agent_id",
                table: "meetings",
                column: "agent_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_members_started_by_id",
                table: "meetings",
                column: "started_by_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_meetings_projects_project_id",
                table: "meetings",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_milestones_projects_project_id",
                table: "milestones",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_project_capacity_projects_project_id",
                table: "project_capacity",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_sprints_projects_project_id",
                table: "sprints",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_transcript_segments_meetings_session_id",
                table: "transcript_segments",
                column: "session_id",
                principalTable: "meetings",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_workspace_exports_members_requested_by_id",
                table: "workspace_exports",
                column: "requested_by_id",
                principalTable: "members",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
